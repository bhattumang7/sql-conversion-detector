using System.CommandLine;
using System.Text.Json;
using System.Text.Json.Serialization;
using SilentScan.Core.Catalog;
using SilentScan.Core.Corpus;
using SilentScan.Core.Lineage;
using SilentScan.Core.Parsing;
using SilentScan.Core.Predicates;
using SilentScan.Core.Reporting;
using SilentScan.Core.Rules;
using SilentScan.Verify.Deployment;
using SilentScan.Verify.Oracle;

namespace SilentScan.Verify.Commands;

/// <summary>
/// `silentscan-verify verify-corpus` — the formal Verify pass for corpus findings (CLAUDE.md
/// Verification workflow): deploys each repo's own schema-shaped DDL (tables, indexes, views,
/// functions - never procedure bodies or DML, enforced by <see cref="DdlStatementWhitelist"/>
/// regardless of which manifest path list a file came from) to a disposable database, then
/// oracle-probes every SCAN_FORCED/RANGE_SEEK finding for that repo via CONVERT_IMPLICIT in
/// plan XML, rather than trusting the static classifier alone. Views/functions declared only
/// under procPaths are deployed too (AFTER ddlPaths, so their table dependencies exist) - a
/// depth&gt;=1 finding's probe queries the view it actually came from, which needs that view to
/// exist as a real deployed object.
/// </summary>
public static class VerifyCorpusCommand
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    public static Command Create()
    {
        var manifestOption = new Option<string>("--manifest")
        {
            Description = "Path to corpus/manifest.json.",
            DefaultValueFactory = _ => "corpus/manifest.json",
        };

        var clonesRootOption = new Option<string>("--clones-root")
        {
            Description = "Directory containing each repo's local clone.",
            DefaultValueFactory = _ => "corpus/_clones",
        };

        var repoOption = new Option<string?>("--repo")
        {
            Description = "Only verify the manifest entry with this name (default: all repos).",
        };

        var command = new Command("verify-corpus", "Oracle-confirm ScanForced/RangeSeek findings for each corpus repo against a disposable SQL Server database.")
        {
            manifestOption,
            clonesRootOption,
            repoOption,
        };

        command.SetAction(async (parseResult, cancellationToken) =>
        {
            var manifestPath = parseResult.GetValue(manifestOption)!;
            var clonesRoot = parseResult.GetValue(clonesRootOption)!;
            var repoFilter = parseResult.GetValue(repoOption);
            return await RunAsync(manifestPath, clonesRoot, repoFilter, SqlServerOptions.LocalDocker, Console.Out, Console.Error, cancellationToken);
        });

        return command;
    }

    internal static async Task<int> RunAsync(
        string manifestPath,
        string clonesRoot,
        string? repoFilter,
        SqlServerOptions sqlOptions,
        TextWriter stdout,
        TextWriter stderr,
        CancellationToken cancellationToken)
    {
        if (!File.Exists(manifestPath))
        {
            await stderr.WriteLineAsync($"error: manifest not found: {manifestPath}");
            return 1;
        }

        var manifest = CorpusManifestLoader.Load(manifestPath);
        var repos = manifest.Repos.Where(r => repoFilter is null || string.Equals(r.Name, repoFilter, StringComparison.OrdinalIgnoreCase)).ToList();
        if (repos.Count == 0)
        {
            await stderr.WriteLineAsync($"error: no manifest entry matches --repo '{repoFilter}'.");
            return 1;
        }

        var context = new VerifyContext(
            new DatabaseProvisioner(sqlOptions), new CorpusFindingVerifier(sqlOptions), new CollationConflictVerifier(sqlOptions),
            new Tier1Verifier(sqlOptions), new ExpressionDerivedVerifier(sqlOptions), new LineageParityChecker(sqlOptions), sqlOptions);
        var summaries = new SortedDictionary<string, RepoVerificationSummary>(StringComparer.Ordinal);
        var hadMissingRepo = false;

        foreach (var repo in repos)
        {
            var repoRoot = Path.Combine(clonesRoot, RepoDirectoryName(repo.Url));
            if (!Directory.Exists(repoRoot))
            {
                await stderr.WriteLineAsync($"warning: '{repo.Name}' has no local clone at {repoRoot} - skipped.");
                hadMissingRepo = true;
                continue;
            }

            await stderr.WriteLineAsync($"Verifying {repo.Name}...");
            var summary = await VerifyRepoAsync(repo, repoRoot, context, cancellationToken);
            summaries[repo.Name] = summary;

            // CLAUDE.md's corpus-admission criterion, actually consulted (an audit finding:
            // ParseSuccessRate existed and was even documented as "the corpus dialect-sniffing
            // signal," but nothing checked it against the >= 90% bar it names).
            if (!summary.PassesDialectSniffing)
            {
                await stderr.WriteLineAsync(
                    $"warning: '{repo.Name}' parse success rate {summary.ParseSuccessRate:P1} is below the " +
                    $"{ParseHealthReport.MinimumAcceptableParseSuccessRate:P0} dialect-sniffing threshold - findings are still reported, but treat them with reduced confidence.");
            }
        }

        await stdout.WriteLineAsync(JsonSerializer.Serialize(summaries, JsonOptions));

        // A run can silently report "success" (exit 0) while having verified nothing - every
        // probe failed, every DDL file refused to deploy, or the lineage parity gate found the
        // static types disagree with sys.columns outright. CI must not go green on that.
        var hasLineageParityFailure = summaries.Values.Any(s => s.LineageParityMismatches.Count > 0);
        var hasZeroEffectiveCoverage = summaries.Values.Any(s =>
            s.ProbeWorthyFindingCount > 0 && s.Confirmed.Count == 0 && s.NotConfirmed.Count == 0 && s.ConfirmedViaScratchIndex.Count == 0);
        var hasDialectSniffingFailure = summaries.Values.Any(s => !s.PassesDialectSniffing);

        return hadMissingRepo || hasLineageParityFailure || hasZeroEffectiveCoverage || hasDialectSniffingFailure ? 1 : 0;
    }

    private sealed record VerifyContext(
        DatabaseProvisioner Provisioner, CorpusFindingVerifier Verifier, CollationConflictVerifier CollationConflictVerifier,
        Tier1Verifier Tier1Verifier, ExpressionDerivedVerifier ExpressionDerivedVerifier, LineageParityChecker ParityChecker, SqlServerOptions SqlOptions);

    private static async Task<RepoVerificationSummary> VerifyRepoAsync(
        CorpusRepoEntry repo,
        string repoRoot,
        VerifyContext context,
        CancellationToken cancellationToken)
    {
        var allFiles = CorpusFileResolver.ResolveAllFiles(repo, repoRoot);
        var parseResults = new List<SqlParseResult>(allFiles.Count);
        foreach (var file in allFiles)
        {
            parseResults.Add(await ParseCorpusFileAsync(repo, file, cancellationToken));
        }

        // Built once and reused for both the report (ScanReportBuilder.BuildFromParseResults no
        // longer builds one internally - roadmap "delete the file-parsed catalog path") and the
        // environment parity gate below, which needs the resolved view provenance to diff
        // against sys.columns after deployment - previously two separate CatalogBuilder.Build
        // calls over the same parse results, wastefully rebuilding the identical catalog twice.
        var usableParseResults = parseResults.Where(r => r.BatchCount > 0).ToList();
        var catalog = CatalogBuilder.Build(usableParseResults, repo.DeclaredCollation, repo.TempdbCollation);
        var lineage = LineageResolver.Resolve(catalog, usableParseResults);

        var report = ScanReportBuilder.BuildFromParseResults(parseResults, catalog);
        var probeWorthy = report.TypedFindings.Where(f => f.Verdict is Verdict.ScanForced or Verdict.RangeSeek).ToList();

        // How many DISTINCT (table, column, operator, other-type) defects probeWorthy actually
        // represents - a repo that re-issues the same CREATE across many incremental upgrade
        // scripts (DNN Platform's 291 .SqlDataProvider files) inflates the raw occurrence count
        // well past the number of real, distinct bugs (CLAUDE.md precision discipline: an
        // occurrence count reported as a prevalence figure is its own kind of false claim).
        // Every occurrence is still individually oracle-probed below - this is reported
        // alongside the raw counts, not used to skip probing any of them.
        var distinctProbeWorthyFindingCount = TypedFindingDeduplicator.Dedupe(probeWorthy).Count;

        var databaseName = SanitizeDatabaseName(repo.Name);
        var deploymentErrors = new List<string>();

        await context.Provisioner.CreateFreshAsync(databaseName, collationName: repo.DeclaredCollation, cancellationToken: cancellationToken);
        try
        {
            var ddlFiles = CorpusFileResolver.ResolveDdlFiles(repo, repoRoot);
            var deployer = new ScriptDeployer(context.SqlOptions);

            // A repo whose manifest keeps views/functions in procPaths, separate from ddlPaths
            // (WideWorldImporters' own Views/*.sql, Functions/*.sql), never had its views
            // deployed at all before this - CorpusFindingProbeBuilder now compiles a depth>=1
            // finding's probe against the view it actually came from, so that view has to exist
            // for the probe to compile. Deploying view/function DEFINITIONS is not "executing
            // the repo's own procedural logic" (CLAUDE.md's ddlPaths-only rule was written to
            // keep DML/procedure BODIES from running, not to leave every view undeployed) - the
            // same whitelist (CreateViewStatement/CreateOrAlterViewStatement/
            // CreateFunctionStatement are already allowed, CreateProcedureStatement is not)
            // still filters out actual procedure bodies at the batch level. Only the files not
            // already covered by ddlFiles (repos where ddlPaths and procPaths are the identical
            // glob - DNN, First Responder Kit, Ola Hallengren - would otherwise deploy the same
            // file twice and fail on "there is already an object named ...").
            var procOnlyFiles = CorpusFileResolver.ResolveProcFiles(repo, repoRoot).Except(ddlFiles, StringComparer.Ordinal);

            // Real-world corpus DDL routinely contains statements our disposable oracle can't
            // (or, per CLAUDE.md's "corpus DML is never executed, anywhere" hard scope, must
            // NOT) execute - permission grants, filegroup references, or an ordinary DML/seed
            // statement sharing a file with real schema DDL. DeployWhitelistedDdlWithRetryAsync
            // is the code-level enforcement of that scope (previously resting entirely on
            // manifest curation): only statement kinds the analysis passes themselves consume
            // actually run. Every file is handed over TOGETHER, not one at a time, so a batch
            // whose foreign key or sequence reference only exists in a file that sorts LATER in
            // glob order (Wide World Importers ships one file per table, each referencing
            // others) simply succeeds on a later retry pass instead of failing outright -
            // ordering across files is not assumed to match dependency order.
            var scripts = new List<(string Label, string Script)>();
            foreach (var ddlFile in ddlFiles)
            {
                var text = CorpusTemplatePreprocessor.Apply(repo.TemplateSubstitutions, SqlScriptParser.DecodeFile(ddlFile));
                scripts.Add((ddlFile, text));
            }

            foreach (var procFile in procOnlyFiles)
            {
                var text = CorpusTemplatePreprocessor.Apply(repo.TemplateSubstitutions, SqlScriptParser.DecodeFile(procFile));
                scripts.Add((procFile, text));
            }

            var batchErrors = await deployer.DeployWhitelistedDdlWithRetryAsync(scripts, databaseName, cancellationToken: cancellationToken);
            deploymentErrors.AddRange(batchErrors);

            // CLAUDE.md Verify workflow: "diff inferred view column types/collations against
            // sys.columns - any mismatch is a P0 lineage bug." Runs after deployment so views
            // actually exist to diff against; a mismatch here means the rest of this repo's
            // findings were reasoned about with wrong column types and cannot be trusted.
            var lineageParityMismatches = await context.ParityChecker.CheckAsync(databaseName, lineage, cancellationToken);

            var results = new List<CorpusFindingResult>();
            foreach (var finding in probeWorthy)
            {
                results.Add(await context.Verifier.VerifyAsync(databaseName, finding, cancellationToken));
            }

            // Every CollationConflictFinding is probe-worthy, unlike TypedFindings' ScanForced/
            // RangeSeek-only filter above - the finding's whole claim (this comparison does not
            // compile) is checkable regardless of verdict, since there is no verdict at all here.
            var collationConflictResults = new List<CollationConflictResult>();
            foreach (var finding in report.CollationConflictFindings)
            {
                collationConflictResults.Add(await context.CollationConflictVerifier.VerifyAsync(databaseName, finding, cancellationToken));
            }

            // Every Tier-1 finding is probe-worthy too - Tier1ProbeBuilder itself reports
            // NotProbeable for anything it can't synthesize a probe for (an unresolved table,
            // no rendered fragment), rather than the caller pre-filtering.
            var tier1Results = new List<Tier1Result>();
            foreach (var finding in report.Tier1Findings)
            {
                tier1Results.Add(await context.Tier1Verifier.VerifyAsync(databaseName, finding, catalog, cancellationToken));
            }

            var expressionDerivedResults = new List<ExpressionDerivedResult>();
            foreach (var finding in report.ExpressionDerivedFindings)
            {
                expressionDerivedResults.Add(await context.ExpressionDerivedVerifier.VerifyAsync(databaseName, finding, cancellationToken));
            }

            return new RepoVerificationSummary(
                TotalDdlFiles: ddlFiles.Count,
                DeploymentErrors: deploymentErrors,
                LineageParityMismatches: lineageParityMismatches,
                ProbeWorthyFindingCount: probeWorthy.Count,
                DistinctProbeWorthyFindingCount: distinctProbeWorthyFindingCount,
                Confirmed: [.. results.Where(r => r.Outcome == CorpusFindingOutcome.Confirmed)],
                NotConfirmed: [.. results.Where(r => r.Outcome == CorpusFindingOutcome.NotConfirmed)],
                NotProbeable: [.. results.Where(r => r.Outcome == CorpusFindingOutcome.NotProbeable)],
                ProbeFailed: [.. results.Where(r => r.Outcome == CorpusFindingOutcome.ProbeFailed)],
                ConfirmedUnindexed: [.. results.Where(r => r.Outcome == CorpusFindingOutcome.ConfirmedUnindexed)],
                ConfirmedViaScratchIndex: [.. results.Where(r => r.Outcome == CorpusFindingOutcome.ConfirmedViaScratchIndex)],
                CollationConflictConfirmed: [.. collationConflictResults.Where(r => r.Outcome == CollationConflictOutcome.Confirmed)],
                CollationConflictNotConfirmed: [.. collationConflictResults.Where(r => r.Outcome == CollationConflictOutcome.NotConfirmed)],
                CollationConflictProbeFailed: [.. collationConflictResults.Where(r => r.Outcome == CollationConflictOutcome.ProbeFailed)],
                Tier1Confirmed: [.. tier1Results.Where(r => r.Outcome == Tier1Outcome.Confirmed)],
                Tier1NotConfirmed: [.. tier1Results.Where(r => r.Outcome == Tier1Outcome.NotConfirmed)],
                Tier1NotProbeable: [.. tier1Results.Where(r => r.Outcome == Tier1Outcome.NotProbeable)],
                Tier1ProbeFailed: [.. tier1Results.Where(r => r.Outcome == Tier1Outcome.ProbeFailed)],
                Tier1ConfirmedUnindexed: [.. tier1Results.Where(r => r.Outcome == Tier1Outcome.ConfirmedUnindexed)],
                Tier1ConfirmedViaScratchIndex: [.. tier1Results.Where(r => r.Outcome == Tier1Outcome.ConfirmedViaScratchIndex)],
                ExpressionDerivedConfirmed: [.. expressionDerivedResults.Where(r => r.Outcome == ExpressionDerivedOutcome.Confirmed)],
                ExpressionDerivedNotConfirmed: [.. expressionDerivedResults.Where(r => r.Outcome == ExpressionDerivedOutcome.NotConfirmed)],
                ExpressionDerivedNotProbeable: [.. expressionDerivedResults.Where(r => r.Outcome == ExpressionDerivedOutcome.NotProbeable)],
                ExpressionDerivedProbeFailed: [.. expressionDerivedResults.Where(r => r.Outcome == ExpressionDerivedOutcome.ProbeFailed)],
                ExpressionDerivedConfirmedUnindexed: [.. expressionDerivedResults.Where(r => r.Outcome == ExpressionDerivedOutcome.ConfirmedUnindexed)],
                DynamicSql: report.DynamicSqlSummary,
                PassesDialectSniffing: report.ParseHealth.PassesDialectSniffing,
                ParseSuccessRate: report.ParseHealth.ParseSuccessRate);
        }
        finally
        {
            await context.Provisioner.DropIfExistsAsync(databaseName, cancellationToken);
        }
    }

    private static Task<SqlParseResult> ParseCorpusFileAsync(CorpusRepoEntry repo, string path, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        // Routes through the same BOM-detection/Latin-1 fallback ParseFile uses (an audit
        // finding: this was bypassing that recovery entirely via a plain File.ReadAllTextAsync).
        var text = SqlScriptParser.DecodeFile(path);
        text = CorpusTemplatePreprocessor.Apply(repo.TemplateSubstitutions, text);
        return Task.FromResult(SqlScriptParser.ParseText(path, text));
    }

    private static string RepoDirectoryName(string url) => url.TrimEnd('/').Split('/')[^1];

    private static string SanitizeDatabaseName(string repoName)
    {
        var sanitized = new string([.. repoName.Select(c => char.IsLetterOrDigit(c) ? c : '_')]);
        return $"SilentScanCorpus_{sanitized}";
    }
}

/// <summary>Per-repo oracle-confirmation outcome (CLAUDE.md: "Study reports only oracle-confirmed findings; static-only findings go in an appendix"), plus how much of the repo's dynamic SQL could be analyzed at all (CLAUDE.md dynamic SQL policy: "X% of procs contain dynamic SQL we could not analyze" - reported, never silently dropped).</summary>
public sealed record RepoVerificationSummary(
    int TotalDdlFiles,
    IReadOnlyList<string> DeploymentErrors,
    IReadOnlyList<LineageParityMismatch> LineageParityMismatches,
    int ProbeWorthyFindingCount,
    int DistinctProbeWorthyFindingCount,
    IReadOnlyList<CorpusFindingResult> Confirmed,
    IReadOnlyList<CorpusFindingResult> NotConfirmed,
    IReadOnlyList<CorpusFindingResult> NotProbeable,
    IReadOnlyList<CorpusFindingResult> ProbeFailed,
    IReadOnlyList<CorpusFindingResult> ConfirmedUnindexed,
    IReadOnlyList<CorpusFindingResult> ConfirmedViaScratchIndex,
    IReadOnlyList<CollationConflictResult> CollationConflictConfirmed,
    IReadOnlyList<CollationConflictResult> CollationConflictNotConfirmed,
    IReadOnlyList<CollationConflictResult> CollationConflictProbeFailed,
    IReadOnlyList<Tier1Result> Tier1Confirmed,
    IReadOnlyList<Tier1Result> Tier1NotConfirmed,
    IReadOnlyList<Tier1Result> Tier1NotProbeable,
    IReadOnlyList<Tier1Result> Tier1ProbeFailed,
    IReadOnlyList<Tier1Result> Tier1ConfirmedUnindexed,
    IReadOnlyList<Tier1Result> Tier1ConfirmedViaScratchIndex,
    IReadOnlyList<ExpressionDerivedResult> ExpressionDerivedConfirmed,
    IReadOnlyList<ExpressionDerivedResult> ExpressionDerivedNotConfirmed,
    IReadOnlyList<ExpressionDerivedResult> ExpressionDerivedNotProbeable,
    IReadOnlyList<ExpressionDerivedResult> ExpressionDerivedProbeFailed,
    IReadOnlyList<ExpressionDerivedResult> ExpressionDerivedConfirmedUnindexed,
    DynamicSqlSummary DynamicSql,
    bool PassesDialectSniffing,
    double ParseSuccessRate,
    int SchemaVersion = ScanReport.CurrentSchemaVersion)
{
    // A Medium-confidence finding (one that rested on a dynamic-SQL placeholder assumption -
    // see FindingConfidence) confirming its own claim against the oracle is real, but it is not
    // the same evidentiary weight as a High-confidence one, and CLAUDE.md's "one false positive
    // in the published study is worse than ten missed true positives" means a published
    // "confirmed" figure must never silently fold the two together. Each computed here, never
    // stored, so a breakdown can never drift out of sync with the list it summarizes - and the
    // raw Confirmed/ConfirmedUnindexed/ConfirmedViaScratchIndex lists stay exactly as they were,
    // so this is additive, never a replacement a consumer has to migrate to.
    public ConfidenceTally ConfirmedByConfidence => ConfidenceTally.Of(Confirmed, r => r.Finding.Confidence);

    public ConfidenceTally ConfirmedUnindexedByConfidence => ConfidenceTally.Of(ConfirmedUnindexed, r => r.Finding.Confidence);

    public ConfidenceTally ConfirmedViaScratchIndexByConfidence => ConfidenceTally.Of(ConfirmedViaScratchIndex, r => r.Finding.Confidence);

    public ConfidenceTally Tier1ConfirmedByConfidence => ConfidenceTally.Of(Tier1Confirmed, r => r.Finding.Confidence);

    public ConfidenceTally Tier1ConfirmedUnindexedByConfidence => ConfidenceTally.Of(Tier1ConfirmedUnindexed, r => r.Finding.Confidence);

    public ConfidenceTally Tier1ConfirmedViaScratchIndexByConfidence => ConfidenceTally.Of(Tier1ConfirmedViaScratchIndex, r => r.Finding.Confidence);

    public ConfidenceTally ExpressionDerivedConfirmedByConfidence => ConfidenceTally.Of(ExpressionDerivedConfirmed, r => r.Finding.Confidence);

    public ConfidenceTally ExpressionDerivedConfirmedUnindexedByConfidence => ConfidenceTally.Of(ExpressionDerivedConfirmedUnindexed, r => r.Finding.Confidence);

    public ConfidenceTally CollationConflictConfirmedByConfidence => ConfidenceTally.Of(CollationConflictConfirmed, r => r.Finding.Confidence);
}

/// <summary>
/// A High/Medium/Low breakdown of a single oracle-confirmed-outcome bucket, computed from the
/// finding-level <see cref="FindingConfidence"/> already carried on each result - never merged
/// back into one number by anything downstream (CLAUDE.md precision discipline: a Medium
/// confirmation and a High confirmation are different claims, not one claim counted twice).
/// </summary>
public readonly record struct ConfidenceTally(int High, int Medium, int Low)
{
    public int Total => High + Medium + Low;

    public static ConfidenceTally Of<T>(IReadOnlyList<T> results, Func<T, FindingConfidence> confidence) => new(
        High: results.Count(r => confidence(r) == FindingConfidence.High),
        Medium: results.Count(r => confidence(r) == FindingConfidence.Medium),
        Low: results.Count(r => confidence(r) == FindingConfidence.Low));
}
