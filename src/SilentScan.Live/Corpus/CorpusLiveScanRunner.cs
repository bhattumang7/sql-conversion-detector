using SilentScan.Core.Corpus;
using SilentScan.Core.Predicates;
using SilentScan.Core.Reporting;
using SilentScan.Verify;
using SilentScan.Verify.Catalog;
using SilentScan.Verify.Corpus;
using SilentScan.Verify.Deployment;

namespace SilentScan.Live.Corpus;

/// <summary>
/// The engine-authoritative corpus path (CLAUDE.md hard scope: "Everything goes via the
/// database — no file-parsed catalog, no file-only scan... corpus scanning deploys the repo's
/// (whitelist-filtered) DDL to the disposable Docker instance, then reads the catalog
/// (LiveCatalogReader) and module text (sys.sql_modules) back out"). Mirrors
/// <see cref="LiveScanRunner"/>'s exact recipe (read catalog from engine metadata, read module
/// text from sys.sql_modules, run the unchanged Lineage/Predicates/Rules pipeline against it),
/// via the shared <see cref="LiveCorpusDeployer"/> - <c>SilentScan.Verify.Commands.VerifyCorpusCommand</c>
/// (a different assembly, <see cref="SilentScan.Verify"/>) uses the exact same deploy-and-read
/// recipe for its own oracle-probing pass, so a schema/collation edge case fixed here is fixed
/// there too, and vice versa.
/// </summary>
public static class CorpusLiveScanRunner
{
    public static async Task<CorpusLiveRepoResult> RunAsync(
        CorpusRepoEntry repo, string repoRoot, SqlServerOptions sqlOptions,
        FindingConfidence minimumConfidence = FindingConfidence.High, CancellationToken cancellationToken = default)
    {
        // GUID-suffixed, matching the OracleTestFixture/TypeMatrixGenerator hygiene fix
        // (docs/local-dev.md) - a fixed per-repo name would collide across two concurrent runs
        // (another session, or scan-corpus-live invoked twice) on the same shared Docker
        // instance exactly like those did before that fix.
        var databaseName = $"{SanitizeDatabaseName(repo.Name)}_{Guid.NewGuid():N}";
        var provisioner = new DatabaseProvisioner(sqlOptions);

        await provisioner.CreateFreshAsync(databaseName, collationName: repo.DeclaredCollation, cancellationToken: cancellationToken);
        try
        {
            var source = await LiveCorpusDeployer.DeployAndReadAsync(repo, repoRoot, databaseName, sqlOptions, cancellationToken);
            var report = ScanReportBuilder.BuildFromParseResults(source.ModuleParseResults, catalog: source.Catalog, minimumConfidence: minimumConfidence)
                with { ParseHealth = ParseHealthReportBuilder.BuildFromParseResults(source.FileParseResults) };

            return new CorpusLiveRepoResult(
                repo, report, LiveCatalogSummary.From(source.Catalog), source.ModuleParseResults.Count,
                source.UnanalyzableModules, source.DeploymentMessages, source.UnmappedModules);
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
