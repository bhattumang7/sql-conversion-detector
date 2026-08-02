using SilentScan.Core.Catalog;
using SilentScan.Core.Lineage;
using SilentScan.Core.Parsing;
using SilentScan.Core.Predicates;
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
    /// <param name="connectionString">Live database connection string.</param>
    /// <param name="includePlanCacheEvidence">
    /// When true, additionally reads the live plan cache (<see cref="LivePlanCacheReader"/>) and
    /// ranks findings by whether they are actually observed converting in a real cached plan,
    /// with execution counts - turning "this predicate could scan" into "this one is scanning
    /// right now". Off by default: it requires <c>VIEW SERVER STATE</c>, a permission a live-mode
    /// caller may not have, and reads a chunk of the plan cache that an ordinary catalog+module
    /// scan has no need to touch.
    /// </param>
    /// <param name="cancellationToken">Cancellation token.</param>
    public static async Task<LiveScanResult> RunAsync(
        string connectionString, bool includePlanCacheEvidence = false, CancellationToken cancellationToken = default)
    {
        var catalog = await new LiveCatalogReader(connectionString).ReadAsync(cancellationToken);
        var moduleResult = await new LiveModuleReader(connectionString).ReadAsync(cancellationToken);

        var parseResults = moduleResult.Modules
            .Select(m => SqlScriptParser.ParseText(m.QualifiedName, m.Definition))
            .ToList();

        // Roadmap Phase C2 (live catalog parity): engine metadata alone knows nothing about
        // temp tables/table variables/TVP shapes or a scalar UDF's return type - those live only
        // as text inside a module body. Running CatalogBuilder over the SAME parsed module
        // bodies (already parsed above for predicate analysis, not reparsed) and merging in only
        // what it can contribute that engine metadata cannot closes the gap that otherwise made
        // a live scan of a synonym/UDF/temp-table-heavy database strictly WORSE than scanning
        // the same objects' scripted-out DDL from disk.
        catalog.MergeFileModeExtras(CatalogBuilder.Build(parseResults));

        // Resolved once here for the parity gate below, and again inside ScanReportBuilder for
        // the findings pipeline itself - a pure function of (catalog, parseResults), so the
        // duplicate resolve costs some CPU but never disagrees with itself; the alternative
        // (threading a pre-resolved LineageCatalog through ScanReportBuilder's public surface)
        // would mean widening a file-mode-only API for a live-mode-only need.
        var lineage = LineageResolver.Resolve(catalog, parseResults);
        var parityMismatches = await new LiveLineageParityChecker(connectionString).CheckAsync(lineage, cancellationToken);

        var report = ScanReportBuilder.BuildFromParseResults(parseResults, catalog: catalog);

        PlanCacheEvidenceResult? planCacheEvidence = null;
        IReadOnlyList<RankedFinding> rankedFindings = [];
        IReadOnlyList<WorkloadFinding> workloadFindings = [];
        if (includePlanCacheEvidence)
        {
            var planCacheReader = new LivePlanCacheReader(connectionString);
            planCacheEvidence = await planCacheReader.ReadObservedConversionsAsync(cancellationToken: cancellationToken);
            rankedFindings = RankByPlanCacheEvidence(report.TypedFindings, planCacheEvidence);

            // Roadmap Phase D: everything above only ranks/confirms findings this scan ALREADY
            // produced from module bodies - an ad-hoc, parameterized application-side query was
            // never a stored procedure body at all, so it never became a TypedPredicateFinding in
            // the first place, no matter how often the plan cache shows it converting. This
            // promotes exactly the (table, column) pairs the module-body pass never covered.
            var alreadyCovered = report.TypedFindings
                .Select(f => (f.Column.TableQualifiedName, f.Column.ColumnName))
                .ToHashSet();
            workloadFindings = await planCacheReader.ReadWorkloadFindingsAsync(catalog, alreadyCovered, cancellationToken: cancellationToken);
        }

        return new LiveScanResult(
            report, LiveCatalogSummary.From(catalog), moduleResult.Modules.Count, parityMismatches,
            moduleResult.Unanalyzable, planCacheEvidence, rankedFindings, workloadFindings);
    }

    private static List<RankedFinding> RankByPlanCacheEvidence(
        IReadOnlyList<TypedPredicateFinding> findings, PlanCacheEvidenceResult evidence)
    {
        // OrderByDescending is a stable sort - ties (including "no plan-cache evidence for
        // either") keep ScanReportBuilder's own CLAUDE.md Pass-4 rank (ScanForced + indexed +
        // depth>=1 first) as the secondary ordering, rather than losing it.
        return findings
            .Select(f =>
            {
                var observed = evidence.TryGetExecutionCount(f.Column.TableQualifiedName, f.Column.ColumnName, out var count);
                return new RankedFinding(f, observed, count);
            })
            .OrderByDescending(r => r.ObservedInLivePlanCache)
            .ThenByDescending(r => r.ObservedExecutionCount)
            .ToList();
    }
}

/// <summary>
/// A live scan's findings plus the catalog-connectivity summary, how many modules were parsed
/// and analyzed, the environment parity gate's result, every module this pass saw but could not
/// read a T-SQL body for (CLR-assembly-backed or encrypted - CLAUDE.md's same-honesty dynamic-
/// SQL rule, applied to modules with no body to analyze at all), and - when requested - the
/// plan-cache ranking signal. CLAUDE.md: "any mismatch is a P0 lineage bug", so
/// <paramref name="LineageParityMismatches"/> is never merely informational; a non-empty list
/// means this run's findings rest on at least one type the pipeline got wrong.
/// </summary>
public sealed record LiveScanResult(
    ScanReport Report,
    LiveCatalogSummary CatalogSummary,
    int ModulesAnalyzed,
    IReadOnlyList<LiveLineageParityMismatch> LineageParityMismatches,
    IReadOnlyList<UnanalyzableModule> UnanalyzableModules,
    PlanCacheEvidenceResult? PlanCacheEvidence,
    IReadOnlyList<RankedFinding> RankedFindings,
    IReadOnlyList<WorkloadFinding> WorkloadFindings);

/// <summary>One static finding plus whether the live plan cache actually shows it converting right now, and how often.</summary>
public sealed record RankedFinding(TypedPredicateFinding Finding, bool ObservedInLivePlanCache, long ObservedExecutionCount);
