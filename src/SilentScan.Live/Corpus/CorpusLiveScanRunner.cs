using SilentScan.Core.Corpus;
using SilentScan.Core.Predicates;
using SilentScan.Core.Reporting;
using SilentScan.Verify;
using SilentScan.Verify.Catalog;
using SilentScan.Verify.Corpus;
using SilentScan.Verify.Deployment;

namespace SilentScan.Live.Corpus;

public static class CorpusLiveScanRunner
{
    public static async Task<CorpusLiveRepoResult> RunAsync(
        CorpusRepoEntry repo, string repoRoot, SqlServerOptions sqlOptions,
        FindingConfidence minimumConfidence = FindingConfidence.High, CancellationToken cancellationToken = default)
    {
        var databaseName = $"{SanitizeDatabaseName(repo.Name)}_{Guid.NewGuid():N}";
        var provisioner = new DatabaseProvisioner(sqlOptions);

        await provisioner.CreateFreshAsync(databaseName, collationName: repo.DeclaredCollation, cancellationToken: cancellationToken);
        try
        {
            var source = await LiveCorpusDeployer.DeployAndReadAsync(repo, repoRoot, databaseName, sqlOptions, cancellationToken);
            var moduleScan = ScanReportBuilder.BuildFromParseResults(source.ModuleParseResults, catalog: source.Catalog, minimumConfidence: minimumConfidence);

            var report = moduleScan with { ParseHealth = ParseHealthReportBuilder.BuildFromParseResults(source.FileParseResults) };

            return new CorpusLiveRepoResult(
                repo, report, LiveCatalogSummary.From(source.Catalog), source.ModuleParseResults.Count,
                source.UnanalyzableModules, source.DeploymentMessages, source.UnmappedModules, moduleScan.ParseHealth);
        }
        finally
        {
            await provisioner.DropIfExistsAsync(databaseName, cancellationToken);
        }
    }

    private static string SanitizeDatabaseName(string repoName)
    {
        var sanitized = new string([.. repoName.Select(c => char.IsLetterOrDigit(c) ? c : '_')]);
        return $"SilentScanCorpusLive_{sanitized}";
    }
}

public sealed record CorpusLiveRepoResult(
    CorpusRepoEntry Repo,
    ScanReport Report,
    LiveCatalogSummary CatalogSummary,
    int ModulesAnalyzed,
    IReadOnlyList<UnanalyzableModule> UnanalyzableModules,
    IReadOnlyList<string> DeploymentMessages,
    IReadOnlyList<string> UnmappedModules,
    ParseHealthReport ModuleParseHealth);
