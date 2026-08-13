using SilentScan.Core.Catalog;
using SilentScan.Core.Diagnostics;
using SilentScan.Core.Lineage;
using SilentScan.Core.Parsing;
using SilentScan.Core.Predicates;
using SilentScan.Core.Reporting;
using SilentScan.Live.Catalog;
using SilentScan.Verify.Catalog;

namespace SilentScan.Live;

/// <summary>
/// Ties the live-database pieces together into the same <see cref="ScanReport"/> shape a
/// file-mode <c>scan</c> produces: read the real catalog from engine metadata
/// (<see cref="LiveCatalogReader"/>), read every readable module body
/// (<see cref="LiveModuleReader"/>), then run the unchanged Lineage/Predicates/Rules pipeline
/// (<see cref="ScanReportBuilder"/>) against the live catalog instead of one inferred from DDL
/// text. A module's findings carry its <c>[schema].[object]</c> qualified name as their source
/// path, so an origin reads as <c>dbo.usp_GetOrders:37</c> - a real line number within the
/// stored module definition. Module bodies are NOT parsed once and held for the run: only the
/// raw module text (<c>modules</c>, cheap - see <c>parseResultSource</c> below) is retained, and
/// every phase that needs the parsed ScriptDOM ASTs reparses fresh from it. A parsed AST runs
/// roughly 200x the size of its source text (measured directly), so holding every module's AST
/// simultaneously for the whole run - what this used to do - made a large database's peak memory
/// scale with its total module text times 200, for the run's entire duration; reparsing per
/// phase instead trades a bounded amount of extra CPU (ScriptDOM reparses quickly) for a peak
/// bounded by the single largest phase's needs rather than their sum.
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
    /// <param name="minimumConfidence">The least confident a finding may be and still appear in the returned report - see <see cref="FindingConfidence"/>. Defaults to <see cref="FindingConfidence.High"/>, unchanged from before this parameter existed.</param>
    /// <param name="progress">
    /// Stage progress sink. A scan of a large database runs for minutes; without this it
    /// produced no output at all until the finished report was rendered, leaving a caller unable
    /// to distinguish a slow stage from a hung one. Defaults to no output.
    /// </param>
    /// <param name="cancellationToken">Cancellation token.</param>
    public static async Task<LiveScanResult> RunAsync(
        string connectionString,
        bool includePlanCacheEvidence = false,
        FindingConfidence minimumConfidence = FindingConfidence.High,
        IScanProgress? progress = null,
        CancellationToken cancellationToken = default)
    {
        progress ??= NullScanProgress.Instance;

        DatabaseCatalog catalog;
        using (var catalogStage = progress.Begin("reading catalog"))
        {
            catalog = await new LiveCatalogReader(connectionString).ReadAsync(cancellationToken);
            catalogStage.Complete($"{catalog.Tables.Count:N0} tables, {catalog.Tables.Sum(t => t.Columns.Count):N0} columns");
        }

        LiveModuleReadResult moduleResult;
        using (var moduleStage = progress.Begin("reading modules"))
        {
            moduleResult = await new LiveModuleReader(connectionString).ReadAsync(cancellationToken);
            moduleStage.Complete($"{moduleResult.Modules.Count:N0} modules");
        }

        var modules = moduleResult.Modules;
        var moduleCount = modules.Count;
        var unanalyzable = moduleResult.Unanalyzable;

        // A lazy, re-enumerable query, not a materialized list: every module's parsed AST runs
        // roughly 200x the size of its source text (measured directly: 12MB of module text
        // peaked at 2.5GB RSS), so holding one List<SqlParseResult> for every module for the
        // WHOLE run - as this used to do - makes that 200x multiplier apply to the entire
        // database's module text simultaneously, for the run's entire duration. modules itself
        // (the raw text, ~200x smaller) is the only thing kept alive here; calling
        // parseResultSource() below re-walks it and reparses from scratch every time, so each of
        // the phases that follows gets its own fresh AST set, individually collectable before the
        // next phase's reparse begins, instead of all of them adding up at once.
        IEnumerable<SqlParseResult> parseResultSource() =>
            modules.AsParallel().AsOrdered()
                .Select(m => SqlScriptParser.ParseText(m.QualifiedName, m.Definition, m.UsesQuotedIdentifier));

        // Roadmap Phase C2 (live catalog parity): engine metadata alone knows nothing about
        // temp tables/table variables/TVP shapes or a scalar UDF's return type - those live only
        // as text inside a module body. Running CatalogBuilder over the SAME parsed module
        // bodies and merging in only what it can contribute that engine metadata cannot closes
        // the gap that otherwise made a live scan of a synonym/UDF/temp-table-heavy database
        // strictly WORSE than scanning the same objects' scripted-out DDL from disk. The real
        // database's own default/tempdb collation (already read by LiveCatalogReader, not a
        // manifest guess) is threaded through as the fallback hint a #temp table/table variable
        // column with no explicit COLLATE of its own needs - without this, every such column's
        // collation stayed permanently unresolved (Verdict.Unknown), since CatalogBuilder had
        // nothing at all to fall back to.
        using (var extrasStage = progress.Begin("merging module-body catalog extras"))
        {
            catalog.MergeFileModeExtras(CatalogBuilder.Build(parseResultSource(), catalog.DefaultCollation?.Name, catalog.TempdbCollation?.Name));
            extrasStage.Complete($"{catalog.Tables.Count:N0} tables");
        }
        PhaseMemory.ReleaseBetweenPhases();

        // Resolved once, here, and handed to ScanReportBuilder below so the findings pipeline
        // reuses this exact instance. Lineage is a pure function of (catalog, parseResultSource()'s
        // output), so resolving it twice never disagreed with itself - but on a large database it
        // is one of the two most expensive passes in the run, and paying for it twice bought
        // nothing.
        LineageCatalog lineage;
        using (var lineageStage = progress.Begin("resolving lineage"))
        {
            lineage = LineageResolver.Resolve(catalog, parseResultSource());
            lineageStage.Complete($"{lineage.AllRelations.Count:N0} relations");
        }
        PhaseMemory.ReleaseBetweenPhases();

        IReadOnlyList<LiveLineageParityMismatch> parityMismatches;
        using (var parityStage = progress.Begin("checking live parity"))
        {
            parityMismatches = await new LiveLineageParityChecker(connectionString).CheckAsync(lineage, cancellationToken);
            parityStage.Complete($"{parityMismatches.Count:N0} mismatches");
        }

        var report = ScanReportBuilder.BuildFromParseResults(
            parseResultSource(), catalog: catalog, minimumConfidence: minimumConfidence, resolvedLineage: lineage, progress: progress);

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
            report, LiveCatalogSummary.From(catalog), moduleCount, parityMismatches,
            unanalyzable, planCacheEvidence, rankedFindings, workloadFindings);
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
