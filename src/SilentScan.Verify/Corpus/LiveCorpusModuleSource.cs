using Microsoft.SqlServer.TransactSql.ScriptDom;
using SilentScan.Core.Catalog;
using SilentScan.Core.Corpus;
using SilentScan.Core.Parsing;
using SilentScan.Verify.Catalog;
using SilentScan.Verify.Deployment;

namespace SilentScan.Verify.Corpus;

/// <summary>
/// Everything a corpus repo's engine-authoritative catalog/module read needs (CLAUDE.md hard
/// scope: "Everything goes via the database — no file-parsed catalog... corpus scanning deploys
/// the repo's (whitelist-filtered) DDL to the disposable Docker instance, then reads the catalog
/// (LiveCatalogReader) and module text (sys.sql_modules) back out"), shared by every corpus
/// consumer that needs a real deployed database to reason from - originally
/// <c>SilentScan.Live.Corpus.CorpusLiveScanRunner</c> alone; <c>VerifyCorpusCommand</c> now uses
/// the identical recipe rather than its own separate (and materially different - file-parsed,
/// procedure bodies never deployed) one. <see cref="Catalog"/> already has
/// <see cref="DatabaseCatalog.MergeFileModeExtras"/> applied - a caller never needs to call it
/// again. <see cref="UnmappedModules"/> should be empty in practice (ledgered, never assumed):
/// every module this same deployment just created ought to trace back to the file that declared
/// it. <see cref="FileParseResults"/> is the RAW repo file parses (pre-deployment) - a caller
/// building dialect-sniffing/<c>SilentScan.Core.Reporting.ParseHealthReport</c> from
/// <see cref="ModuleParseResults"/> instead would silently miss a file that fails to deploy
/// entirely (not T-SQL at all), the exact case dialect sniffing exists to catch; use this field
/// for that, not <see cref="ModuleParseResults"/> (<c>SilentScan.Core.Reporting.ParseHealthReport</c>).
/// </summary>
public sealed record LiveCorpusModuleSource(
    DatabaseCatalog Catalog,
    IReadOnlyList<SqlParseResult> ModuleParseResults,
    IReadOnlyList<SqlParseResult> FileParseResults,
    IReadOnlyList<string> DeploymentMessages,
    IReadOnlyList<UnanalyzableModule> UnanalyzableModules,
    IReadOnlyList<string> UnmappedModules);

public static class LiveCorpusDeployer
{
    /// <summary>
    /// Deploys <paramref name="repo"/>'s own DDL and proc/view/function/trigger files (in that
    /// order - a proc referencing a table only ddlPaths itself declares must see it exist first)
    /// to the ALREADY-PROVISIONED <paramref name="databaseName"/>, then reads catalog and module
    /// text back from the engine. <c>allowProcedureAndTriggerDefinitions: true</c> is deliberate,
    /// not a relaxation of CLAUDE.md's "corpus DML and procs are never executed" - deploying a
    /// CREATE PROCEDURE/TRIGGER's own DEFINITION is not executing its body (the whitelist still
    /// filters out every actual DML/EXEC statement inside one, exactly as it always did for a
    /// view's or function's body), and without it no module text for a proc's own dynamic SQL
    /// would ever exist in <c>sys.sql_modules</c> to read back at all.
    /// </summary>
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
                    // Genuinely shouldn't happen for anything this same run just deployed -
                    // ledgered rather than silently trusted, per CLAUDE.md's "never silently
                    // counted as clean." Falls back to the bare qualified name, exactly
                    // LiveScanRunner's own (permanent, not a degrade) source path for a live
                    // target with no file to map back to at all.
                    unmappedModules.Add(module.QualifiedName);
                    sourcePath = module.QualifiedName;
                }

                return SqlScriptParser.ParseText(sourcePath, module.Definition, module.UsesQuotedIdentifier);
            })
            .ToList();

        // Same reasoning as LiveScanRunner: engine metadata alone knows nothing about temp
        // tables/table variables/TVP shapes or a scalar UDF's return type declared inside a
        // module body - only the module text itself carries those. Built from the POST-
        // DEPLOYMENT module parse results (not the raw repo files) so a temp table declared
        // inside a proc body is resolved from the exact same text the predicate pipeline below
        // reads. Falls back to the real deployed database's own collation
        // (catalog.DefaultCollation/TempdbCollation, read by LiveCatalogReader) when the manifest
        // declares none.
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

    /// <summary>
    /// Maps every procedure/view/function/trigger name declared anywhere across
    /// <paramref name="fileParseResults"/> to the file that declares it - later files in the SAME
    /// order deployment uses win over earlier ones, matching real deployment semantics exactly: a
    /// repo that re-declares the same object across several incremental-upgrade files (DNN
    /// Platform's *.SqlDataProvider pattern) ends up with exactly one row in the deployed
    /// database's own <c>sys.sql_modules</c> - whichever file's CREATE/ALTER ran last - so the
    /// provenance map's own "last file wins" rule is not a simplification, it is what actually
    /// happened.
    /// </summary>
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
