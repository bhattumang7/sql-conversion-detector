using SilentScan.Core.Catalog;
using SilentScan.Core.Diagnostics;
using SilentScan.Core.Lineage;
using SilentScan.Core.Parsing;
using SilentScan.Core.Predicates;
using SilentScan.Core.Rules;

namespace SilentScan.Core.Reporting;

public static class ScanReportBuilder
{
    /// <summary>
    /// Builds a report from already-parsed sources. <paramref name="catalog"/> is REQUIRED
    /// (roadmap "delete the file-parsed catalog path and the file-only scan pipeline" - CLAUDE.md
    /// hard scope: "Everything goes via the database — no file-parsed catalog, no file-only
    /// scan") - this method no longer infers one from <paramref name="allParseResults"/> itself.
    /// Every real caller reads the catalog from a live database's own metadata
    /// (<c>SilentScan.Live.Catalog.LiveCatalogReader</c>, via <c>LiveScanRunner</c>/
    /// <c>CorpusLiveScanRunner</c>) - the only place file-parsed catalog inference
    /// (<see cref="CatalogBuilder"/>) still runs at all is <c>DatabaseCatalog.MergeFileModeExtras</c>,
    /// contributing what engine metadata alone cannot see (temp tables, table variables, a scalar
    /// UDF's return type) on top of that real catalog, never in place of it.
    /// </summary>
    /// <param name="allParseResults">Already-parsed sources to run Lineage/Predicates/Rules over.</param>
    /// <param name="catalog">The real catalog, read from a live database's own metadata.</param>
    public static ScanReport BuildFromParseResults(IReadOnlyList<SqlParseResult> allParseResults, DatabaseCatalog catalog)
    {
        var fileHealth = new List<FileParseHealth>();
        var usableParseResults = new List<SqlParseResult>();

        foreach (var result in allParseResults)
        {
            var errors = result.Errors
                .Select(e => new ParseErrorInfo(e.Line, e.Column, e.Number, e.Message))
                .ToList();
            fileHealth.Add(new FileParseHealth(result.SourcePath, errors, result.BatchCount));

            // A batch containing a syntax error is dropped by ScriptDOM itself, not the whole
            // file (docs/audit-remediation-plan.md Phase 4.4, audit finding B4) - excluding the
            // whole file whenever Errors was non-empty threw away every OTHER batch's tables/
            // views/procs too, even when they parsed perfectly cleanly. A file contributes
            // whatever batches it has left; one with zero surviving batches contributes nothing
            // either way.
            if (result.BatchCount > 0)
            {
                usableParseResults.Add(result);
            }
        }

        // Lineage needs every cleanly-parsed file together, so views can resolve against tables
        // (and other views) declared in a different file. Resolved before Tier-1 scanning (which
        // used to run catalog-blind) so a syntactic finding's column can be resolved through the
        // same machinery Pass 3/4 use, carrying real Indexed/TableQualifiedName information
        // instead of none at all. Also resolved before the dynamic SQL scan below - the call
        // graph a proc-body parameter seed needs (ProcCallGraphBuilder.Build) requires
        // TryGetProcedureParameters, which requires the catalog the caller already supplied.
        var lineage = LineageResolver.Resolve(catalog, usableParseResults);

        var callGraphLedger = new SkipLedger();
        var procCallGraph = ProcCallGraphBuilder.Build(usableParseResults, catalog, callGraphLedger);

        var dynamicSqlExtractions = usableParseResults.Select(r => DynamicSqlScanner.Scan(r, callGraph: procCallGraph)).ToList();
        var dynamicSqlFindings = dynamicSqlExtractions.SelectMany(r => r.Findings).ToList();
        var dynamicSqlScripts = dynamicSqlExtractions.SelectMany(r => r.AnalyzableScripts).ToList();

        // Pass 1 (CatalogBuilder) resolves a SELECT ... INTO target's columns against tables
        // already known to the catalog only - views/CTEs/UNION sources are a Pass 2 concept
        // catalog-building can't depend on without inverting the pass order. Re-resolves those
        // targets now that lineage exists, mutating the same catalog instance every pass below
        // reads (SelectIntoLineagePass docs: closes a silent-drop gap, not just an Unknown one).
        SelectIntoLineagePass.Apply(catalog, lineage, usableParseResults);

        // catalog/lineage are read-only from this point on (SelectIntoLineagePass.Apply just
        // above was the last write to catalog; nothing here mutates either) - every file's
        // Tier-1 scan and typed extraction is fully independent of every other file's, so both
        // run in parallel (CLAUDE.md roadmap: "scale the scan pipeline"). Each file gets its OWN
        // ledger/result rather than sharing one mutable accumulator across threads (SkipLedger is
        // explicitly not thread-safe), merged back together afterward; final findings are sorted
        // by source path/line below regardless, so the parallel completion order never leaks into
        // the report's own deterministic ordering.
        var tier1PerFile = usableParseResults
            .AsParallel()
            .Select(r =>
            {
                var fileLedger = new SkipLedger();
                var findings = NonSargablePredicateScanner.Scan(r, catalog, lineage, ledger: fileLedger).ToList();
                return (Findings: findings, Skipped: fileLedger.Entries);
            })
            .ToList();
        var tier1Findings = tier1PerFile.SelectMany(p => p.Findings).ToList();
        var tier1SkippedEntries = tier1PerFile.SelectMany(p => p.Skipped).ToList();
        var extractionResults = usableParseResults.AsParallel().Select(r => TypedPredicateExtractor.Extract(r, catalog, lineage)).ToList();
        var typedFindings = extractionResults.SelectMany(r => r.TypedFindings).ToList();
        var expressionDerivedFindings = extractionResults.SelectMany(r => r.ExpressionDerivedFindings).ToList();
        var collationConflictFindings = extractionResults.SelectMany(r => r.CollationConflictFindings).ToList();
        var writeLossFindings = extractionResults.SelectMany(r => r.WriteLossFindings).ToList();
        var skippedConstructs = new List<SkippedConstruct>();
        skippedConstructs.AddRange(catalog.Skipped.Entries);
        skippedConstructs.AddRange(lineage.Skipped.Entries);
        skippedConstructs.AddRange(callGraphLedger.Entries);
        skippedConstructs.AddRange(tier1SkippedEntries);
        skippedConstructs.AddRange(extractionResults.SelectMany(r => r.SkippedConstructs));

        // Tier A of the dynamic SQL policy (CLAUDE.md): reparse provably-constant EXEC/
        // sp_executesql arguments through the same pipeline and fold their findings in,
        // remapped back to their true source location.
        var dynamicSqlResult = DynamicSqlPipeline.Analyze(dynamicSqlScripts, catalog, lineage);
        dynamicSqlFindings = [.. dynamicSqlFindings, .. dynamicSqlResult.Findings];
        tier1Findings = [.. tier1Findings, .. dynamicSqlResult.Tier1Findings];
        typedFindings = [.. typedFindings, .. dynamicSqlResult.TypedFindings];
        expressionDerivedFindings = [.. expressionDerivedFindings, .. dynamicSqlResult.ExpressionDerivedFindings];
        collationConflictFindings = [.. collationConflictFindings, .. dynamicSqlResult.CollationConflictFindings];
        writeLossFindings = [.. writeLossFindings, .. dynamicSqlResult.WriteLossFindings];
        skippedConstructs.AddRange(dynamicSqlResult.SkippedConstructs);

        // Captured before SeekPreserved findings are dropped below - the report's only
        // denominator for "N flagged out of M comparisons classified" (CLAUDE.md precision
        // discipline: a bare finding count with no base rate can't be checked against
        // anything).
        var typedPredicateSummary = TypedPredicateSummary.From(typedFindings);

        // Previously computed only by VerifyCorpusCommand - a plain `scan`/`scan-corpus` run
        // carried the raw DynamicSqlFindings list with no rollup anywhere in its own output (an
        // audit finding), so a reader had to hand-count outcomes to get the "X% of dynamic SQL
        // call sites we could not analyze" figure CLAUDE.md's dynamic SQL policy requires.
        var dynamicSqlSummary = DynamicSqlSummary.From(dynamicSqlFindings);

        typedFindings = [.. typedFindings.Where(f => f.Verdict != Verdict.SeekPreserved)];

        // Deterministic output ordering (CLAUDE.md), then CLAUDE.md's Pass 4 rank:
        // SCAN_FORCED + indexed + depth>=1 first.
        tier1Findings = [.. tier1Findings.OrderBy(f => f.SourcePath, StringComparer.Ordinal).ThenBy(f => f.Line).ThenBy(f => f.Column)];
        dynamicSqlFindings = [.. dynamicSqlFindings.OrderBy(f => f.SourcePath, StringComparer.Ordinal).ThenBy(f => f.Line)];
        typedFindings = [.. typedFindings
            .OrderBy(f => VerdictRank(f.Verdict))
            .ThenByDescending(f => f.Column.Indexed)
            .ThenByDescending(f => f.Column.Depth)
            .ThenBy(f => f.SourcePath, StringComparer.Ordinal)
            .ThenBy(f => f.Line)];
        expressionDerivedFindings = [.. expressionDerivedFindings.OrderBy(f => f.SourcePath, StringComparer.Ordinal).ThenBy(f => f.Line)];
        collationConflictFindings = [.. collationConflictFindings.OrderBy(f => f.SourcePath, StringComparer.Ordinal).ThenBy(f => f.Line)];
        writeLossFindings = [.. writeLossFindings.OrderBy(f => f.SourcePath, StringComparer.Ordinal).ThenBy(f => f.Line)];
        var orderedSkippedConstructs = skippedConstructs
            .OrderBy(s => s.Pass)
            .ThenBy(s => s.SourcePath, StringComparer.Ordinal)
            .ThenBy(s => s.Line)
            .ThenBy(s => s.Column)
            .ToList();

        return new ScanReport(
            new ParseHealthReport(fileHealth), tier1Findings, typedFindings, dynamicSqlFindings, expressionDerivedFindings, collationConflictFindings, writeLossFindings,
            orderedSkippedConstructs, SkippedConstructSummary.From(orderedSkippedConstructs), typedPredicateSummary, dynamicSqlSummary);
    }

    private static int VerdictRank(Verdict verdict) => verdict switch
    {
        Verdict.ScanForced => 0,
        Verdict.RangeSeek => 1,
        Verdict.Unknown => 2,
        _ => 3,
    };
}
