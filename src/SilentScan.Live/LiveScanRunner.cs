using SilentScan.Core.Lineage;
using SilentScan.Core.Parsing;
using SilentScan.Core.Reporting;
using SilentScan.Live.Catalog;

namespace SilentScan.Live;

/// <summary>
/// Ties the live-database pieces together into the same <see cref="ScanReport"/> shape a
/// file-mode <c>scan</c> produces: read the real catalog from engine metadata
/// (<see cref="LiveCatalogReader"/>), read every readable module body
/// (<see cref="LiveModuleReader"/>), parse each with the same ScriptDOM parser file-mode uses,
/// and run the unchanged Lineage/Predicates/Rules pipeline (<see cref="ScanReportBuilder"/>)
/// against the live catalog instead of one inferred from DDL text. A module's findings carry
/// its <c>[schema].[object]</c> qualified name as their source path, so an origin reads as
/// <c>dbo.usp_GetOrders:37</c> - a real line number within the stored module definition.
/// </summary>
public static class LiveScanRunner
{
    public static async Task<LiveScanResult> RunAsync(string connectionString, CancellationToken cancellationToken = default)
    {
        var catalog = await new LiveCatalogReader(connectionString).ReadAsync(cancellationToken);
        var modules = await new LiveModuleReader(connectionString).ReadAsync(cancellationToken);

        var parseResults = modules
            .Select(m => SqlScriptParser.ParseText(m.QualifiedName, m.Definition))
            .ToList();

        // Resolved once here for the parity gate below, and again inside ScanReportBuilder for
        // the findings pipeline itself - a pure function of (catalog, parseResults), so the
        // duplicate resolve costs some CPU but never disagrees with itself; the alternative
        // (threading a pre-resolved LineageCatalog through ScanReportBuilder's public surface)
        // would mean widening a file-mode-only API for a live-mode-only need.
        var lineage = LineageResolver.Resolve(catalog, parseResults);
        var parityMismatches = await new LiveLineageParityChecker(connectionString).CheckAsync(lineage, cancellationToken);

        var report = ScanReportBuilder.BuildFromParseResults(parseResults, catalog: catalog);

        return new LiveScanResult(report, LiveCatalogSummary.From(catalog), modules.Count, parityMismatches);
    }
}

/// <summary>
/// A live scan's findings plus the catalog-connectivity summary, how many modules were parsed
/// and analyzed, and the environment parity gate's result - CLAUDE.md: "any mismatch is a P0
/// lineage bug", so <paramref name="LineageParityMismatches"/> is never merely informational; a
/// non-empty list means this run's findings rest on at least one type the pipeline got wrong.
/// </summary>
public sealed record LiveScanResult(
    ScanReport Report,
    LiveCatalogSummary CatalogSummary,
    int ModulesAnalyzed,
    IReadOnlyList<LiveLineageParityMismatch> LineageParityMismatches);
