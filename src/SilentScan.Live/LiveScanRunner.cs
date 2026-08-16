using Microsoft.Data.SqlClient;
using SilentScan.Core.Catalog;
using SilentScan.Core.Diagnostics;
using SilentScan.Core.Lineage;
using SilentScan.Core.Parsing;
using SilentScan.Core.Predicates;
using SilentScan.Core.Predicates.DynamicSqlValue;
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
    /// <param name="fetchSqlFromTables">
    /// When true, additionally lets the dynamic-SQL engine's own SELECT-assignment splice
    /// (<c>SELECT @sql = Definition FROM dbo.Templates WHERE Name = 'X'</c>) fetch the real
    /// value from this target once the WHERE clause pins the row down to a literal key, instead
    /// of leaving it a RowDependentColumn hole - see <see cref="LiveTableRowValueFetcher"/>. Off
    /// by default: it reads real row content (not just catalog metadata) from a user table, a
    /// meaningfully bigger read than every other live probe this tool issues, so it stays opt-in
    /// even though `scan-db` targets a development/staging database the user is already working
    /// against, not an untrusted one.
    /// </param>
    /// <param name="cancellationToken">Cancellation token.</param>
    public static async Task<LiveScanResult> RunAsync(
        string connectionString,
        bool includePlanCacheEvidence = false,
        FindingConfidence minimumConfidence = FindingConfidence.High,
        IScanProgress? progress = null,
        bool fetchSqlFromTables = false,
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

        // docs/detection-checklist.md Tier 1 "SET options that silently disable plan features" -
        // QUOTED_IDENTIFIER OFF is baked in wholesale at CREATE/ALTER compile time
        // (sys.sql_modules.uses_quoted_identifier), already read by LiveModuleReader for parsing
        // purposes alone; registered here so a later rule can query it per-module too, instead of
        // the flag being read once and then discarded.
        foreach (var module in modules)
        {
            catalog.AddModuleUsesQuotedIdentifier(module.QualifiedName, module.UsesQuotedIdentifier);
            catalog.AddModuleUsesAnsiNulls(module.QualifiedName, module.UsesAnsiNulls);
        }

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

        // A #temp table CREATEd only inside a dynamically-built SQL string (SET @ddl = @ddl +
        // 'CREATE TABLE #Runs (...)'; EXEC (@ddl)) is invisible to the static-body pass just
        // above - it has no literal CreateTableStatement anywhere in the AST. Best-effort,
        // catalog-free constant-folding (this pass runs before the real catalog is even
        // complete) recovers the common case: a chain of literal concatenation building the
        // whole DDL text inside one proc. See DynamicSqlTempTableDiscovery's own doc comment.
        using (var dynamicExtrasStage = progress.Begin("discovering dynamic-SQL temp tables"))
        {
            var discovered = DynamicSqlTempTableDiscovery.Discover(parseResultSource(), catalog.DefaultCollation?.Name, catalog.TempdbCollation?.Name);
            catalog.MergeFileModeExtras(discovered);
            dynamicExtrasStage.Complete($"{catalog.Tables.Count:N0} tables");
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

        LiveLineageParityReport parity;
        using (var parityStage = progress.Begin("checking live parity"))
        {
            parity = await new LiveLineageParityChecker(connectionString).CheckAsync(lineage, cancellationToken);
            parityStage.Complete(
                $"{parity.Mismatches.Count:N0} mismatches, {parity.UncompilableObjects.Count:N0} uncompilable, " +
                $"{parity.StaleCachedMetadata.Count:N0} stale, {parity.Unverified.Count:N0} unverified");
        }

        ScanReport report;
        if (fetchSqlFromTables)
        {
            await using var fetchConnection = new SqlConnection(connectionString);
            await fetchConnection.OpenAsync(cancellationToken);
            var rowValueFetcher = new LiveTableRowValueFetcher(fetchConnection);
            report = ScanReportBuilder.BuildFromParseResults(
                parseResultSource(), catalog: catalog, minimumConfidence: minimumConfidence, resolvedLineage: lineage, progress: progress,
                rowValueFetcher: rowValueFetcher);
        }
        else
        {
            report = ScanReportBuilder.BuildFromParseResults(
                parseResultSource(), catalog: catalog, minimumConfidence: minimumConfidence, resolvedLineage: lineage, progress: progress);
        }

        // docs/detection-checklist.md Tier 2 "Dynamic SQL quality" item 3: INSERT INTO #temp EXEC
        // proc's shape mismatch needs its own live round trip per call site
        // (sys.dm_exec_describe_first_result_set) - not something ScanReportBuilder's own
        // catalog+lineage pipeline above can produce, so it's computed here and merged into the
        // report afterward, the same shape LineageParity already uses for a live-only concern.
        TempTableExecShapeReport tempTableExecShape;
        using (var tempTableStage = progress.Begin("checking INSERT...EXEC temp-table shapes"))
        {
            var candidates = parseResultSource()
                .SelectMany(r => TempTableExecShapeCandidateScanner.Scan(r, catalog))
                .ToList();
            tempTableExecShape = await new TempTableExecShapeChecker(connectionString).CheckAsync(candidates, cancellationToken);
            report = report with { TempTableExecShapeFindings = tempTableExecShape.Findings };
            tempTableStage.Complete($"{tempTableExecShape.Findings.Count:N0} findings, {tempTableExecShape.Unanalyzed.Count:N0} unanalyzed");
        }
        PhaseMemory.ReleaseBetweenPhases();

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
            report, LiveCatalogSummary.From(catalog), moduleCount, parity,
            unanalyzable, planCacheEvidence, rankedFindings, workloadFindings, tempTableExecShape);
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
/// plan-cache ranking signal. <paramref name="LineageParity"/> is never merely informational: a
/// non-empty <see cref="LiveLineageParityReport.Mismatches"/> means this run's findings rest on
/// at least one type the pipeline got wrong, verified against what the engine computes for that
/// object right now - not against its cached <c>sys.columns</c> metadata, which can go stale
/// without being a tool bug (see <see cref="LiveLineageParityChecker"/>). The
/// <see cref="TempTableExecShapeReport"/> findings folded into <see cref="Report"/>'s own
/// <c>TempTableExecShapeFindings</c> are exposed again, whole, so its <c>Unanalyzed</c> list
/// (every <c>INSERT INTO #temp EXEC proc</c> site this pass declined to judge, and why) is
/// reachable at all - the same "findings in the report, honesty list beside it" split
/// <see cref="LineageParity"/> already uses.
/// </summary>
public sealed record LiveScanResult(
    ScanReport Report,
    LiveCatalogSummary CatalogSummary,
    int ModulesAnalyzed,
    LiveLineageParityReport LineageParity,
    IReadOnlyList<UnanalyzableModule> UnanalyzableModules,
    PlanCacheEvidenceResult? PlanCacheEvidence,
    IReadOnlyList<RankedFinding> RankedFindings,
    IReadOnlyList<WorkloadFinding> WorkloadFindings,
    TempTableExecShapeReport TempTableExecShape);

/// <summary>One static finding plus whether the live plan cache actually shows it converting right now, and how often.</summary>
public sealed record RankedFinding(TypedPredicateFinding Finding, bool ObservedInLivePlanCache, long ObservedExecutionCount);
