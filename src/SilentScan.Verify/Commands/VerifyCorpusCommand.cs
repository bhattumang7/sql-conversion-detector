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
            new Tier1Verifier(sqlOptions), new ExpressionDerivedVerifier(sqlOptions), new TvfFenceVerifier(sqlOptions), new ScalarUdfVerifier(sqlOptions), new LineageParityChecker(sqlOptions), sqlOptions);
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

            if (!summary.PassesDialectSniffing)
            {
                await stderr.WriteLineAsync(
                    $"warning: '{repo.Name}' parse success rate {summary.ParseSuccessRate:P1} is below the " +
                    $"{ParseHealthReport.MinimumAcceptableParseSuccessRate:P0} dialect-sniffing threshold - findings are still reported, but treat them with reduced confidence.");
            }
        }

        await stdout.WriteLineAsync(JsonSerializer.Serialize(summaries, JsonOptions));

        var hasLineageParityFailure = summaries.Values.Any(s => s.LineageParityMismatches.Count > 0);
        var hasZeroEffectiveCoverage = summaries.Values.Any(s =>
            s.ProbeWorthyFindingCount > 0 && s.Confirmed.Count == 0 && s.NotConfirmed.Count == 0
            && s.ConfirmedViaScratchIndex.Count == 0 && s.ConfirmedUnindexed.Count == 0);
        var hasDialectSniffingFailure = summaries.Values.Any(s => !s.PassesDialectSniffing);

        return hadMissingRepo || hasLineageParityFailure || hasZeroEffectiveCoverage || hasDialectSniffingFailure ? 1 : 0;
    }

    private sealed record VerifyContext(
        DatabaseProvisioner Provisioner, CorpusFindingVerifier Verifier, CollationConflictVerifier CollationConflictVerifier,
        Tier1Verifier Tier1Verifier, ExpressionDerivedVerifier ExpressionDerivedVerifier, TvfFenceVerifier TvfFenceVerifier, ScalarUdfVerifier ScalarUdfVerifier, LineageParityChecker ParityChecker, SqlServerOptions SqlOptions);

    private static async Task<RepoVerificationSummary> VerifyRepoAsync(
        CorpusRepoEntry repo,
        string repoRoot,
        VerifyContext context,
        FindingConfidence minimumConfidence,
        CancellationToken cancellationToken)
    {
        var ddlFiles = CorpusFileResolver.ResolveDdlFiles(repo, repoRoot);

        var databaseName = $"{SanitizeDatabaseName(repo.Name)}_{Guid.NewGuid():N}";
        var deploymentErrors = new List<string>();

        await context.Provisioner.CreateFreshAsync(databaseName, collationName: repo.DeclaredCollation, cancellationToken: cancellationToken);
        try
        {
            var source = await LiveCorpusDeployer.DeployAndReadAsync(repo, repoRoot, databaseName, context.SqlOptions, cancellationToken);
            deploymentErrors.AddRange(source.DeploymentMessages);

            var catalog = source.Catalog;

            var lineage = LineageResolver.Resolve(catalog, source.ModuleParseResults);

            var moduleScan = ScanReportBuilder.BuildFromParseResults(source.ModuleParseResults, catalog, minimumConfidence);

            var moduleParseHealth = moduleScan.ParseHealth;
            var report = moduleScan with { ParseHealth = ParseHealthReportBuilder.BuildFromParseResults(source.FileParseResults) };
            var probeWorthy = report.TypedFindings.Where(f => f.Verdict is Verdict.ScanForced or Verdict.RangeSeek).ToList();

            var distinctProbeWorthyFindingCount = TypedFindingDeduplicator.Dedupe(probeWorthy).Count;

            var lineageParityMismatches = await context.ParityChecker.CheckAsync(databaseName, lineage, cancellationToken);

            var results = new List<CorpusFindingResult>();
            foreach (var finding in probeWorthy)
            {
                results.Add(await context.Verifier.VerifyAsync(databaseName, finding, cancellationToken));
            }

            var collationConflictResults = new List<CollationConflictResult>();
            foreach (var finding in report.CollationConflictFindings)
            {
                collationConflictResults.Add(await context.CollationConflictVerifier.VerifyAsync(databaseName, finding, cancellationToken));
            }

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

            var tvfFenceResults = new List<TvfFenceResult>();
            foreach (var finding in report.TvfFenceFindings)
            {
                tvfFenceResults.Add(await context.TvfFenceVerifier.VerifyAsync(databaseName, finding, cancellationToken));
            }

            var scalarUdfResults = new List<ScalarUdfResult>();
            foreach (var finding in report.ScalarUdfFindings)
            {
                scalarUdfResults.Add(await context.ScalarUdfVerifier.VerifyAsync(databaseName, finding, cancellationToken));
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
                Tier1UnindexedNotProbeable: [.. tier1Results.Where(r => r.Outcome == Tier1Outcome.UnindexedNotProbeable)],
                Tier1ConfirmedViaScratchIndex: [.. tier1Results.Where(r => r.Outcome == Tier1Outcome.ConfirmedViaScratchIndex)],
                ExpressionDerivedConfirmed: [.. expressionDerivedResults.Where(r => r.Outcome == ExpressionDerivedOutcome.Confirmed)],
                ExpressionDerivedNotConfirmed: [.. expressionDerivedResults.Where(r => r.Outcome == ExpressionDerivedOutcome.NotConfirmed)],
                ExpressionDerivedNotProbeable: [.. expressionDerivedResults.Where(r => r.Outcome == ExpressionDerivedOutcome.NotProbeable)],
                ExpressionDerivedProbeFailed: [.. expressionDerivedResults.Where(r => r.Outcome == ExpressionDerivedOutcome.ProbeFailed)],
                ExpressionDerivedUnindexedNotProbeable: [.. expressionDerivedResults.Where(r => r.Outcome == ExpressionDerivedOutcome.UnindexedNotProbeable)],
                TvfFenceConfirmed: [.. tvfFenceResults.Where(r => r.Outcome == TvfFenceOutcome.Confirmed)],
                TvfFenceNotConfirmed: [.. tvfFenceResults.Where(r => r.Outcome == TvfFenceOutcome.NotConfirmed)],
                TvfFenceNotProbeable: [.. tvfFenceResults.Where(r => r.Outcome == TvfFenceOutcome.NotProbeable)],
                TvfFenceProbeFailed: [.. tvfFenceResults.Where(r => r.Outcome == TvfFenceOutcome.ProbeFailed)],
                ScalarUdfConfirmed: [.. scalarUdfResults.Where(r => r.Outcome == ScalarUdfOutcome.Confirmed)],
                ScalarUdfNotConfirmed: [.. scalarUdfResults.Where(r => r.Outcome == ScalarUdfOutcome.NotConfirmed)],
                ScalarUdfNotProbeable: [.. scalarUdfResults.Where(r => r.Outcome == ScalarUdfOutcome.NotProbeable)],
                ScalarUdfProbeFailed: [.. scalarUdfResults.Where(r => r.Outcome == ScalarUdfOutcome.ProbeFailed)],
                DynamicSql: report.DynamicSqlSummary,
                PassesDialectSniffing: report.ParseHealth.PassesDialectSniffing,
                ParseSuccessRate: report.ParseHealth.ParseSuccessRate,
                ModulesWithReparseErrors: [.. moduleParseHealth.Files.Where(f => f.Errors.Count > 0).Select(f => f.Path)]);
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
    IReadOnlyList<Tier1Result> Tier1UnindexedNotProbeable,
    IReadOnlyList<Tier1Result> Tier1ConfirmedViaScratchIndex,
    IReadOnlyList<ExpressionDerivedResult> ExpressionDerivedConfirmed,
    IReadOnlyList<ExpressionDerivedResult> ExpressionDerivedNotConfirmed,
    IReadOnlyList<ExpressionDerivedResult> ExpressionDerivedNotProbeable,
    IReadOnlyList<ExpressionDerivedResult> ExpressionDerivedProbeFailed,
    IReadOnlyList<ExpressionDerivedResult> ExpressionDerivedUnindexedNotProbeable,
    IReadOnlyList<TvfFenceResult> TvfFenceConfirmed,
    IReadOnlyList<TvfFenceResult> TvfFenceNotConfirmed,
    IReadOnlyList<TvfFenceResult> TvfFenceNotProbeable,
    IReadOnlyList<TvfFenceResult> TvfFenceProbeFailed,
    IReadOnlyList<ScalarUdfResult> ScalarUdfConfirmed,
    IReadOnlyList<ScalarUdfResult> ScalarUdfNotConfirmed,
    IReadOnlyList<ScalarUdfResult> ScalarUdfNotProbeable,
    IReadOnlyList<ScalarUdfResult> ScalarUdfProbeFailed,
    DynamicSqlSummary DynamicSql,
    bool PassesDialectSniffing,
    double ParseSuccessRate,
    IReadOnlyList<string> ModulesWithReparseErrors,
    int SchemaVersion = ScanReport.CurrentSchemaVersion)
{
    public ConfidenceTally ConfirmedByConfidence => ConfidenceTally.Of(Confirmed, r => r.Finding.Confidence);

    public ConfidenceTally ConfirmedUnindexedByConfidence => ConfidenceTally.Of(ConfirmedUnindexed, r => r.Finding.Confidence);

    public ConfidenceTally ConfirmedViaScratchIndexByConfidence => ConfidenceTally.Of(ConfirmedViaScratchIndex, r => r.Finding.Confidence);

    public ConfidenceTally Tier1ConfirmedByConfidence => ConfidenceTally.Of(Tier1Confirmed, r => r.Finding.Confidence);

    public ConfidenceTally Tier1UnindexedNotProbeableByConfidence => ConfidenceTally.Of(Tier1UnindexedNotProbeable, r => r.Finding.Confidence);

    public ConfidenceTally Tier1ConfirmedViaScratchIndexByConfidence => ConfidenceTally.Of(Tier1ConfirmedViaScratchIndex, r => r.Finding.Confidence);

    public ConfidenceTally ExpressionDerivedConfirmedByConfidence => ConfidenceTally.Of(ExpressionDerivedConfirmed, r => r.Finding.Confidence);

    public ConfidenceTally ExpressionDerivedUnindexedNotProbeableByConfidence => ConfidenceTally.Of(ExpressionDerivedUnindexedNotProbeable, r => r.Finding.Confidence);

    public ConfidenceTally CollationConflictConfirmedByConfidence => ConfidenceTally.Of(CollationConflictConfirmed, r => r.Finding.Confidence);

    public ConfidenceTally TvfFenceConfirmedByConfidence => ConfidenceTally.Of(TvfFenceConfirmed, r => r.Finding.Confidence);

    public ConfidenceTally ScalarUdfConfirmedByConfidence => ConfidenceTally.Of(ScalarUdfConfirmed, r => r.Finding.Confidence);
}

public readonly record struct ConfidenceTally(int High, int Medium, int Low)
{
    public int Total => High + Medium + Low;

    public static ConfidenceTally Of<T>(IReadOnlyList<T> results, Func<T, FindingConfidence> confidence) => new(
        High: results.Count(r => confidence(r) == FindingConfidence.High),
        Medium: results.Count(r => confidence(r) == FindingConfidence.Medium),
        Low: results.Count(r => confidence(r) == FindingConfidence.Low));
}
