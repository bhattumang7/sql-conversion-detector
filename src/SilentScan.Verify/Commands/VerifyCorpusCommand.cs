using System.CommandLine;
using System.Text.Json;
using System.Text.Json.Serialization;
using SilentScan.Core.Corpus;
using SilentScan.Core.Lineage;
using SilentScan.Core.Predicates;
using SilentScan.Core.Reporting;
using SilentScan.Core.Rules;
using SilentScan.Verify.Corpus;
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

        var confidenceOption = new Option<string>("--confidence")
        {
            Description = FindingConfidenceParsing.OptionDescription,
            DefaultValueFactory = _ => "high",
        };

        var command = new Command("verify-corpus", "Oracle-confirm ScanForced/RangeSeek findings for each corpus repo against a disposable SQL Server database.")
        {
            manifestOption,
            clonesRootOption,
            repoOption,
            confidenceOption,
        };

        command.SetAction(async (parseResult, cancellationToken) =>
        {
            var options = new VerifyCorpusOptions(
                parseResult.GetValue(manifestOption)!,
                parseResult.GetValue(clonesRootOption)!,
                parseResult.GetValue(repoOption),
                parseResult.GetValue(confidenceOption)!);
            return await RunAsync(options, SqlServerOptions.LocalDocker, Console.Out, Console.Error, cancellationToken);
        });

        return command;
    }

    /// <summary>The flags <c>verify-corpus</c>'s own <c>RunAsync</c> takes together - bundled into one value so a caller doesn't add a bare parameter for every new flag and blow through Sonar's per-method parameter budget, the same pattern <c>SilentScan.Cli</c>'s own ReportOptions uses.</summary>
    internal readonly record struct VerifyCorpusOptions(string ManifestPath, string ClonesRoot, string? RepoFilter, string Confidence);

    internal static async Task<int> RunAsync(
        VerifyCorpusOptions options,
        SqlServerOptions sqlOptions,
        TextWriter stdout,
        TextWriter stderr,
        CancellationToken cancellationToken)
    {
        var (manifestPath, clonesRoot, repoFilter, confidence) = options;

        if (!File.Exists(manifestPath))
        {
            await stderr.WriteLineAsync($"error: manifest not found: {manifestPath}");
            return 1;
        }

        if (!FindingConfidenceParsing.TryParse(confidence, out var minimumConfidence))
        {
            await stderr.WriteLineAsync(FindingConfidenceParsing.UnknownConfidenceMessage(confidence));
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
            var summary = await VerifyRepoAsync(repo, repoRoot, context, minimumConfidence, cancellationToken);
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
        // ConfirmedUnindexed counts as real coverage too (an audit finding: it didn't, even
        // though it IS the oracle confirming CONVERT_IMPLICIT on the column - the same signal
        // Confirmed/ConfirmedViaScratchIndex represent, just without the RangeSeek-vs-ScanForced
        // plan-shape distinction an absent index makes untestable) - the overwhelmingly common
        // real-world outcome for a corpus's own DDL, which rarely indexes every column its own
        // predicates compare.
        var hasLineageParityFailure = summaries.Values.Any(s => s.LineageParityMismatches.Count > 0);
        var hasZeroEffectiveCoverage = summaries.Values.Any(s =>
            s.ProbeWorthyFindingCount > 0 && s.Confirmed.Count == 0 && s.NotConfirmed.Count == 0
            && s.ConfirmedViaScratchIndex.Count == 0 && s.ConfirmedUnindexed.Count == 0);
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
        FindingConfidence minimumConfidence,
        CancellationToken cancellationToken)
    {
        var ddlFiles = CorpusFileResolver.ResolveDdlFiles(repo, repoRoot);

        // GUID-suffixed, matching CorpusLiveScanRunner's own concurrency-safety fix
        // (docs/local-dev.md) - a fixed per-repo name would collide across two concurrent runs
        // (another session, or verify-corpus/scan-corpus-live invoked simultaneously) on the same
        // shared Docker instance exactly like those did before that fix.
        var databaseName = $"{SanitizeDatabaseName(repo.Name)}_{Guid.NewGuid():N}";
        var deploymentErrors = new List<string>();

        await context.Provisioner.CreateFreshAsync(databaseName, collationName: repo.DeclaredCollation, cancellationToken: cancellationToken);
        try
        {
            // CLAUDE.md hard scope: "Everything goes via the database — no file-parsed catalog,
            // no file-only scan... corpus scanning deploys the repo's DDL to the disposable
            // Docker instance, then reads the catalog (LiveCatalogReader) and module text
            // (sys.sql_modules) back out." The same deploy-and-read recipe scan-corpus-live uses
            // (LiveCorpusDeployer, shared with CorpusLiveScanRunner in a different assembly) -
            // not a separate, lower-fidelity file-parsed catalog that never deploys procedure
            // bodies at all and therefore can never see a proc's own dynamic SQL.
            var source = await LiveCorpusDeployer.DeployAndReadAsync(repo, repoRoot, databaseName, context.SqlOptions, cancellationToken);
            deploymentErrors.AddRange(source.DeploymentMessages);

            var catalog = source.Catalog;

            // Lineage needs the same POST-DEPLOYMENT module parse results the report itself is
            // built from below, since the environment parity gate needs to diff a view's own
            // resolved provenance against sys.columns for the exact objects sys.sql_modules just
            // handed back - not a separate, independently re-parsed set.
            var lineage = LineageResolver.Resolve(catalog, source.ModuleParseResults);

            var report = ScanReportBuilder.BuildFromParseResults(source.ModuleParseResults, catalog, minimumConfidence)
                with { ParseHealth = ParseHealthReportBuilder.BuildFromParseResults(source.FileParseResults) };
            var probeWorthy = report.TypedFindings.Where(f => f.Verdict is Verdict.ScanForced or Verdict.RangeSeek).ToList();

            // How many DISTINCT (table, column, operator, other-type) defects probeWorthy
            // actually represents - sys.sql_modules already collapses a repo that re-issues the
            // same CREATE across many incremental-upgrade files (DNN Platform's *.SqlDataProvider
            // pattern) to one row per object, but a repo can still declare the same real defect
            // more than once across genuinely different objects (CLAUDE.md precision discipline:
            // an occurrence count reported as a prevalence figure is its own kind of false
            // claim). Every occurrence is still individually oracle-probed below - this is
            // reported alongside the raw counts, not used to skip probing any of them.
            var distinctProbeWorthyFindingCount = TypedFindingDeduplicator.Dedupe(probeWorthy).Count;

            // CLAUDE.md Verify workflow: "diff inferred view column types/collations against
            // sys.columns - any mismatch is a P0 lineage bug." A mismatch here means the rest of
            // this repo's findings were reasoned about with wrong column types and cannot be
            // trusted.
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

    private static string RepoDirectoryName(string url) => url.TrimEnd('/').Split('/')[^1];

    private static string SanitizeDatabaseName(string repoName)
    {
        var sanitized = new string([.. repoName.Select(c => char.IsLetterOrDigit(c) ? c : '_')]);
        return $"SilentScanCorpusVerify_{sanitized}";
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
