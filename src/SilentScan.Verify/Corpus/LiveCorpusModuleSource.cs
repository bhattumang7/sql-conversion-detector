using Microsoft.SqlServer.TransactSql.ScriptDom;
using SilentScan.Core.Catalog;
using SilentScan.Core.Corpus;
using SilentScan.Core.Parsing;
using SilentScan.Verify.Catalog;
using SilentScan.Verify.Deployment;
using SilentScan.Core.Common;

namespace SilentScan.Verify.Corpus;

public sealed record LiveCorpusModuleSource(
    DatabaseCatalog Catalog,
    IReadOnlyList<SqlParseResult> ModuleParseResults,
    IReadOnlyList<SqlParseResult> FileParseResults,
    IReadOnlyList<string> DeploymentMessages,
    IReadOnlyList<UnanalyzableModule> UnanalyzableModules,
    IReadOnlyList<string> UnmappedModules);

public static class LiveCorpusDeployer
{
    public static async Task<LiveCorpusModuleSource> DeployAndReadAsync(
        CorpusRepoEntry repo, string repoRoot, string databaseName, SqlServerOptions sqlOptions, CancellationToken cancellationToken)
    {
        var ddlFiles = CorpusFileResolver.ResolveDdlFiles(repo, repoRoot);
        var procOnlyFiles = CorpusFileResolver.ResolveProcFiles(repo, repoRoot).Except(ddlFiles, StringComparer.Ordinal).ToList();
        var orderedFiles = ddlFiles.Concat(procOnlyFiles).ToList();

        var fileParseResults = orderedFiles.Select(f => ParseCorpusFile(repo, f)).ToList();
        var provenance = BuildProvenanceMap(fileParseResults);

        var deployer = new ScriptDeployer(sqlOptions);
        var scripts = new List<(string Label, string Script)>(orderedFiles.Count);
        foreach (var file in orderedFiles)
        {
            var text = CorpusTemplatePreprocessor.Apply(repo.TemplateSubstitutions, SqlScriptParser.DecodeFile(file));
            scripts.Add((file, text));
        }

        var deploymentMessages = await deployer.DeployWhitelistedDdlWithRetryAsync(
            scripts, databaseName, allowProcedureAndTriggerDefinitions: true, cancellationToken: cancellationToken);

        var connectionString = sqlOptions.BuildConnectionString(databaseName);
        var catalog = await new LiveCatalogReader(connectionString).ReadAsync(cancellationToken);
        var moduleResult = await new LiveModuleReader(connectionString).ReadAsync(cancellationToken);

        var unmappedModules = new List<string>();
        var moduleParseResults = moduleResult.Modules
            .Select(module =>
            {
                if (!provenance.TryGetValue(module.QualifiedName, out var sourcePath))
                {

                    unmappedModules.Add(module.QualifiedName);
                    sourcePath = module.QualifiedName;
                }

                return SqlScriptParser.ParseText(sourcePath, module.Definition, module.UsesQuotedIdentifier, catalog.CompatibilityLevel);
            })
            .ToList();

        catalog.MergeFileModeExtras(CatalogBuilder.Build(
            moduleParseResults,
            repo.DeclaredCollation ?? catalog.DefaultCollation?.Name,
            repo.TempdbCollation ?? catalog.TempdbCollation?.Name));

        return new LiveCorpusModuleSource(catalog, moduleParseResults, fileParseResults, deploymentMessages, moduleResult.Unanalyzable, unmappedModules);
    }

    private static SqlParseResult ParseCorpusFile(CorpusRepoEntry repo, string path)
    {
        var text = SqlScriptParser.DecodeFile(path);
        text = CorpusTemplatePreprocessor.Apply(repo.TemplateSubstitutions, text);
        return SqlScriptParser.ParseText(path, text);
    }

    private static Dictionary<string, string> BuildProvenanceMap(IReadOnlyList<SqlParseResult> fileParseResults)
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var result in fileParseResults)
        {
            if (result.Fragment is not TSqlScript script)
            {
                continue;
            }

            var collector = new ModuleNameCollector();
            script.Accept(collector);
            foreach (var qualifiedName in collector.Names)
            {
                map[qualifiedName] = result.SourcePath;
            }
        }

        return map;
    }

    private sealed class ModuleNameCollector : TSqlFragmentVisitor
    {
        public List<string> Names { get; } = [];

        public override void Visit(CreateProcedureStatement node) => Names.Add(SchemaObjectNameHelper.Qualify(node.ProcedureReference.Name));

        public override void Visit(AlterProcedureStatement node) => Names.Add(SchemaObjectNameHelper.Qualify(node.ProcedureReference.Name));

        public override void Visit(CreateOrAlterProcedureStatement node) => Names.Add(SchemaObjectNameHelper.Qualify(node.ProcedureReference.Name));

        public override void Visit(CreateViewStatement node) => Names.Add(SchemaObjectNameHelper.Qualify(node.SchemaObjectName));

        public override void Visit(AlterViewStatement node) => Names.Add(SchemaObjectNameHelper.Qualify(node.SchemaObjectName));

        public override void Visit(CreateOrAlterViewStatement node) => Names.Add(SchemaObjectNameHelper.Qualify(node.SchemaObjectName));

        public override void Visit(CreateFunctionStatement node) => Names.Add(SchemaObjectNameHelper.Qualify(node.Name));

        public override void Visit(AlterFunctionStatement node) => Names.Add(SchemaObjectNameHelper.Qualify(node.Name));

        public override void Visit(CreateOrAlterFunctionStatement node) => Names.Add(SchemaObjectNameHelper.Qualify(node.Name));

        public override void Visit(CreateTriggerStatement node) => Names.Add(SchemaObjectNameHelper.Qualify(node.Name));

        public override void Visit(AlterTriggerStatement node) => Names.Add(SchemaObjectNameHelper.Qualify(node.Name));

        public override void Visit(CreateOrAlterTriggerStatement node) => Names.Add(SchemaObjectNameHelper.Qualify(node.Name));
    }
}
