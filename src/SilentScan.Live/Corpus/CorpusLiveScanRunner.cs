using Microsoft.SqlServer.TransactSql.ScriptDom;
using SilentScan.Core.Catalog;
using SilentScan.Core.Corpus;
using SilentScan.Core.Lineage;
using SilentScan.Core.Parsing;
using SilentScan.Core.Reporting;
using SilentScan.Live.Catalog;
using SilentScan.Verify;
using SilentScan.Verify.Deployment;

namespace SilentScan.Live.Corpus;

/// <summary>
/// The engine-authoritative corpus path (CLAUDE.md hard scope: "Everything goes via the
/// database — no file-parsed catalog, no file-only scan... corpus scanning deploys the repo's
/// (whitelist-filtered) DDL to the disposable Docker instance, then reads the catalog
/// (LiveCatalogReader) and module text (sys.sql_modules) back out"). Mirrors
/// <see cref="LiveScanRunner"/>'s exact recipe (read catalog from engine metadata, read module
/// text from sys.sql_modules, run the unchanged Lineage/Predicates/Rules pipeline against it) but
/// adds the one step a live target database never needs: deploying a repo's own DDL/proc files
/// to a fresh disposable database first, and mapping each deployed module back to the REAL repo
/// file that defines it - CLAUDE.md: "Corpus findings still map back to the defining repo file,
/// since the study cites repos," which a bare <c>[schema].[object]</c> qualified name (all
/// <see cref="LiveScanRunner"/> itself needs, since a live target has no source file at all)
/// would not satisfy on its own.
/// </summary>
public static class CorpusLiveScanRunner
{
    public static async Task<CorpusLiveRepoResult> RunAsync(
        CorpusRepoEntry repo, string repoRoot, SqlServerOptions sqlOptions, CancellationToken cancellationToken = default)
    {
        var ddlFiles = CorpusFileResolver.ResolveDdlFiles(repo, repoRoot);
        var procOnlyFiles = CorpusFileResolver.ResolveProcFiles(repo, repoRoot).Except(ddlFiles, StringComparer.Ordinal).ToList();
        var orderedFiles = ddlFiles.Concat(procOnlyFiles).ToList();

        var fileParseResults = orderedFiles.Select(f => ParseCorpusFile(repo, f)).ToList();
        var provenance = BuildProvenanceMap(fileParseResults);

        // GUID-suffixed, matching the OracleTestFixture/TypeMatrixGenerator hygiene fix
        // (docs/local-dev.md) - a fixed per-repo name would collide across two concurrent runs
        // (another session, or scan-corpus-live invoked twice) on the same shared Docker
        // instance exactly like those did before that fix.
        var databaseName = $"{SanitizeDatabaseName(repo.Name)}_{Guid.NewGuid():N}";
        var provisioner = new DatabaseProvisioner(sqlOptions);
        var deployer = new ScriptDeployer(sqlOptions);

        await provisioner.CreateFreshAsync(databaseName, collationName: repo.DeclaredCollation, cancellationToken: cancellationToken);
        try
        {
            var scripts = new List<(string Label, string Script)>(orderedFiles.Count);
            foreach (var file in orderedFiles)
            {
                var text = CorpusTemplatePreprocessor.Apply(repo.TemplateSubstitutions, SqlScriptParser.DecodeFile(file));
                scripts.Add((file, text));
            }

            // allowProcedureAndTriggerDefinitions: true - the one deliberate difference from
            // verify-corpus's own deployment (DdlStatementWhitelist's doc comment explains why
            // this is still pure DDL, never an execution of the body). Every actual DML/EXEC
            // statement inside a proc/trigger body is still opaque batch text to this whitelist,
            // exactly as a view's or function's body already was before this existed - nothing
            // about "corpus DML and procs are never executed" changes.
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

                    return SqlScriptParser.ParseText(sourcePath, module.Definition);
                })
                .ToList();

            // Same reasoning as LiveScanRunner: engine metadata alone knows nothing about temp
            // tables/table variables/TVP shapes or a scalar UDF's return type declared inside a
            // module body - only the module text itself carries those. Built from the POST-
            // DEPLOYMENT module parse results (not the raw repo files) so a temp table declared
            // inside a proc body is resolved from the exact same text the predicate pipeline
            // below reads, matching LiveScanRunner's own pattern precisely.
            catalog.MergeFileModeExtras(CatalogBuilder.Build(moduleParseResults, repo.DeclaredCollation, repo.TempdbCollation));

            var report = ScanReportBuilder.BuildFromParseResults(moduleParseResults, catalog: catalog);

            return new CorpusLiveRepoResult(
                repo, report, LiveCatalogSummary.From(catalog), moduleResult.Modules.Count,
                moduleResult.Unanalyzable, deploymentMessages, unmappedModules);
        }
        finally
        {
            await provisioner.DropIfExistsAsync(databaseName, cancellationToken);
        }
    }

    private static SqlParseResult ParseCorpusFile(CorpusRepoEntry repo, string path)
    {
        var text = SqlScriptParser.DecodeFile(path);
        text = CorpusTemplatePreprocessor.Apply(repo.TemplateSubstitutions, text);
        return SqlScriptParser.ParseText(path, text);
    }

    /// <summary>
    /// Maps every procedure/view/function/trigger name declared anywhere across
    /// <paramref name="fileParseResults"/> to the file that declares it - later files in the
    /// SAME order deployment uses win over earlier ones, matching real deployment semantics
    /// exactly: a repo that re-declares the same object across several incremental-upgrade files
    /// (DNN Platform's *.SqlDataProvider pattern) ends up with exactly one row in the deployed
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

    private static string SanitizeDatabaseName(string repoName)
    {
        var sanitized = new string([.. repoName.Select(c => char.IsLetterOrDigit(c) ? c : '_')]);
        return $"SilentScanCorpusLive_{sanitized}";
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

/// <summary>
/// One corpus repo's engine-authoritative scan: the same <see cref="ScanReport"/> shape every
/// other scan mode produces, plus how many modules deployed/read successfully, every module the
/// engine could not read a body for (CLR/encrypted - never silently dropped), every deployment
/// message (a batch skipped by the whitelist, or one that failed even after retries), and every
/// live-read module this run could not map back to the repo file that declares it (should be
/// empty in practice - ledgered rather than assumed).
/// </summary>
public sealed record CorpusLiveRepoResult(
    CorpusRepoEntry Repo,
    ScanReport Report,
    LiveCatalogSummary CatalogSummary,
    int ModulesAnalyzed,
    IReadOnlyList<UnanalyzableModule> UnanalyzableModules,
    IReadOnlyList<string> DeploymentMessages,
    IReadOnlyList<string> UnmappedModules);
