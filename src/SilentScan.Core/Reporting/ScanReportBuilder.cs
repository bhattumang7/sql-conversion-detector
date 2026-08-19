using SilentScan.Core.Catalog;
using SilentScan.Core.Diagnostics;
using SilentScan.Core.Lineage;
using SilentScan.Core.Parsing;
using SilentScan.Core.Predicates;
using SilentScan.Core.Predicates.DynamicSqlValue;
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
    /// (<c>SilentScan.Verify.Catalog.LiveCatalogReader</c>, via <c>LiveScanRunner</c>/
    /// <c>CorpusLiveScanRunner</c>) - the only place file-parsed catalog inference
    /// (<see cref="CatalogBuilder"/>) still runs at all is <c>DatabaseCatalog.MergeFileModeExtras</c>,
    /// contributing what engine metadata alone cannot see (temp tables, table variables, a scalar
    /// UDF's return type) on top of that real catalog, never in place of it.
    /// </summary>
    /// <param name="allParseResults">Already-parsed sources to run Lineage/Predicates/Rules over.</param>
    /// <param name="catalog">The real catalog, read from a live database's own metadata.</param>
    /// <param name="minimumConfidence">
    /// The least confident a finding may be and still appear in the returned report - see
    /// <see cref="FindingConfidence"/>. Defaults to <see cref="FindingConfidence.High"/>, matching
    /// this method's behavior before the field existed: nothing here is filtered out unless a
    /// caller explicitly opts into a lower tier. Applied after <see cref="TypedPredicateSummary"/>
    /// is computed, so that summary's own denominator - "how many comparisons were classified at
    /// all" - stays complete regardless of what the caller chooses to have reported.
    /// </param>
    /// <param name="resolvedLineage">
    /// An already-resolved lineage catalog for exactly these parse results and this catalog, when
    /// the caller has one. <c>LiveScanRunner</c> must resolve lineage itself to run the live
    /// parity gate before the report is built; passing that same instance back in here avoids
    /// resolving it a second time, which on a large database is one of the two most expensive
    /// passes run twice for no benefit. Pass <see langword="null"/> (the default) and this
    /// method resolves its own, exactly as it always has.
    /// </param>
    /// <param name="progress">Stage progress sink; defaults to no output.</param>
    /// <param name="rowValueFetcher">
    /// scan-db's own opt-in <c>--fetch-sql-from-tables</c> live row fetch (<see
    /// cref="ILiveRowValueFetcher"/>) - lets the dynamic-SQL engine's SELECT-assignment splice
    /// resolve a real value instead of a RowDependentColumn hole when the WHERE clause pins the
    /// row down to a literal key. Null (the default, and every corpus/file-mode caller) leaves
    /// that shape exactly as it was - purely additive precision, never a soundness requirement.
    /// </param>
    public static ScanReport BuildFromParseResults(
        IEnumerable<SqlParseResult> allParseResults,
        DatabaseCatalog catalog,
        FindingConfidence minimumConfidence = FindingConfidence.High,
        LineageCatalog? resolvedLineage = null,
        IScanProgress? progress = null,
        ILiveRowValueFetcher? rowValueFetcher = null)
    {
        progress ??= NullScanProgress.Instance;
        var fileHealth = new List<FileParseHealth>();

        // Parsed once here, then shared by every phase below. This deliberately used to stay a
        // lazy query: a live scan hands in a re-enumerable source that reparses each module from
        // cheap retained text (LiveScanRunner), so never materializing meant never holding every
        // module's AST at once. Measured directly, that traded away far more than it bought. The
        // ~50 phases below each re-enumerate this source, so every module was parsed 50 times per
        // scan; at the scale the design was written for (800 modules, ~12MB of module text) that
        // ran 380s and allocated 87GB, against 50s and 4GB for holding one AST set, while peaking
        // at 1,683MB versus 1,778MB - a 5.6% memory difference for a 7.6x runtime one. Holding the
        // ASTs is the cheaper side of that trade by a wide margin, and it removes the bulk of the
        // per-phase garbage that PhaseMemory's forced collections existed to clear.
        var parseResults = allParseResults as IReadOnlyList<SqlParseResult> ?? allParseResults.ToList();

        // AST-free in the sense that still matters: only the source path of every usable file
        // survives into usableSourcePaths, not the SqlParseResult/Fragment itself.
        var (usableSourcePaths, usableCount) = CollectUsableSourcePaths(parseResults, fileHealth);

        // Declared once and reused BY REFERENCE so every phase below reads identically to before.
        IReadOnlyList<SqlParseResult> usableParseResults =
            parseResults.Where(r => usableSourcePaths.Contains(r.SourcePath)).ToList();

        // Lineage needs every cleanly-parsed file together, so views can resolve against tables
        // (and other views) declared in a different file. Resolved before Tier-1 scanning (which
        // used to run catalog-blind) so a syntactic finding's column can be resolved through the
        // same machinery Pass 3/4 use, carrying real Indexed/TableQualifiedName information
        // instead of none at all. Also resolved before the dynamic SQL scan below - the call
        // graph a proc-body parameter seed needs (ProcCallGraphBuilder.Build) requires
        // TryGetProcedureParameters, which requires the catalog the caller already supplied.
        LineageCatalog lineage;
        if (resolvedLineage is not null)
        {
            lineage = resolvedLineage;
        }
        else
        {
            using var lineageStage = progress.Begin("resolving lineage");
            lineage = LineageResolver.Resolve(catalog, usableParseResults);
            lineageStage.Complete($"{lineage.AllRelations.Count:N0} relations");
        }

        // MSTVF-as-fence stream (docs/detection-checklist.md Tier 1 #2): which view/inline-TVF
        // definitions inherit a multi-statement/CLR TVF fence from somewhere inside their own
        // body. A second, small extraction pass over the same parse results - LineageResolver
        // doesn't expose the ViewDefinition list it builds internally, and re-deriving it here
        // is cheap next to the passes either side of it.
        IReadOnlyDictionary<string, Lineage.TvfFenceOrigin> tvfFenceMap;
        IReadOnlyDictionary<string, Lineage.ScalarUdfOrigin> scalarUdfMap;
        IReadOnlyDictionary<string, Lineage.ViewExpansionOrigin> viewExpansionMap;
        IReadOnlyDictionary<string, SelectStarViewCandidate> selectStarViewCandidates;
        List<Lineage.ViewDefinition> viewDefinitions;
        using (var fenceMapStage = progress.Begin("mapping TVF fences and scalar UDFs"))
        {
            (viewDefinitions, _) = Lineage.ViewDefinitionExtractor.Extract(usableParseResults, catalog.DefaultCollation, catalog.TypeAliases, ledger: null);
            tvfFenceMap = Lineage.TvfFenceMap.Build(viewDefinitions, catalog);
            scalarUdfMap = Lineage.ScalarUdfMap.Build(viewDefinitions, catalog);
            viewExpansionMap = Lineage.ViewExpansionMap.Build(viewDefinitions, catalog);
            selectStarViewCandidates = SelectStarViewScanner.BuildCandidates(viewDefinitions, viewExpansionMap, lineage);
            fenceMapStage.Complete($"{tvfFenceMap.Count:N0} view/TVF layers inherit a fence, {scalarUdfMap.Count:N0} inherit a scalar UDF");
        }

        var callGraphLedger = new SkipLedger();
        ProcCallGraph procCallGraph;
        using (var callGraphStage = progress.Begin("building call graph"))
        {
            procCallGraph = ProcCallGraphBuilder.Build(usableParseResults, catalog, callGraphLedger);
            callGraphStage.Complete($"{procCallGraph.Edges.Count:N0} edges");
        }
        PhaseMemory.ReleaseBetweenPhases();

        // OUTPUT-parameter tracking (roadmap "trace a constant OUTPUT value across a proc-call
        // edge"): an ordinary `EXEC dbo.Helper @out = @var OUTPUT` can only seed the CALLER's
        // @var from a summary of what dbo.Helper's own body always assigns its OUTPUT parameter -
        // which this same scan produces as a side effect of walking dbo.Helper's body for its OWN
        // EXEC sites. A single pass can only feed a callee's summary forward to a caller scanned
        // AFTER it; re-running with the summaries seen so far closes that regardless of file/proc
        // order, and running until no NEW summary appears (capped, matching this codebase's other
        // bounded-recursion limits) also resolves a short OUTPUT-through-OUTPUT chain, not just
        // one hop. The FINAL pass below is the one whose findings/scripts are actually reported.
        var dynamicSqlExtractions = ScanDynamicSqlWithOutputSummaries(
            usableParseResults, procCallGraph, catalog, rowValueFetcher, usableCount, progress);
        PhaseMemory.ReleaseBetweenPhases();

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
        // #temp tables are session-scoped in real SQL Server (visible to a callee EXEC'd from
        // the proc that created them), unlike a table variable (always proc-local, never
        // propagated) - a "driver" proc that creates #Results and EXECs several sub-procs
        // against it is common, real corpus code. Every distinct caller scope is carried here,
        // not just a single one - the consumer (FromScopeResolver/TypedPredicateExtractor) tries
        // each caller's own scoped catalog entry for the SPECIFIC name being resolved and only
        // uses the result when every caller that has one agrees on its exact shape
        // (CatalogTable.HasSameShapeAs); when they disagree, or when this is the only caller
        // known for one #temp name but not another, resolution still declines rather than guess.
        // Computed once here, before EITHER Tier-1 or the typed pass runs, so both benefit
        // identically - a #temp table's own resolvability shouldn't depend on which pass asks.
        var callerScopeByCalleeScope = new Dictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var group in procCallGraph.Edges
                     .Where(e => e.CallerScopeQualifiedName is not null)
                     .GroupBy(e => e.CalleeQualifiedName, StringComparer.OrdinalIgnoreCase))
        {
            callerScopeByCalleeScope[group.Key] = group.Select(e => e.CallerScopeQualifiedName!).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        }

        List<(List<SargabilityFinding> Findings, List<TemporalBoundaryPrecisionFinding> TemporalBoundaryFindings, IReadOnlyList<SkippedConstruct> Skipped)> tier1PerFile;
        using (var tier1Stage = progress.Begin("scanning syntactic predicates", usableCount))
        {
            tier1PerFile = usableParseResults
                .AsParallel()
                .Select(r =>
                {
                    var fileLedger = new SkipLedger();
                    var (findings, temporalBoundaryFindings) = NonSargablePredicateScanner.ScanFull(r, catalog, lineage, ledger: fileLedger, callerScopeByCalleeScope: callerScopeByCalleeScope);
                    tier1Stage.Advance();
                    return (Findings: findings.ToList(), TemporalBoundaryFindings: temporalBoundaryFindings.ToList(), Skipped: fileLedger.Entries);
                })
                .ToList();
        }

        var tier1Findings = tier1PerFile.SelectMany(p => p.Findings).ToList();
        var temporalBoundaryFindings = tier1PerFile.SelectMany(p => p.TemporalBoundaryFindings)
            .OrderBy(f => f.SourcePath, StringComparer.Ordinal).ThenBy(f => f.Line).ThenBy(f => f.Column).ToList();
        var tier1SkippedEntries = tier1PerFile.SelectMany(p => p.Skipped).ToList();
        PhaseMemory.ReleaseBetweenPhases();

        List<TvfFenceFinding> tvfFenceFindings;
        using (var tvfFenceStage = progress.Begin("scanning TVF fences", usableCount))
        {
            tvfFenceFindings = usableParseResults
                .AsParallel()
                .SelectMany(r =>
                {
                    var findings = TvfFenceScanner.Scan(r, catalog, tvfFenceMap);
                    tvfFenceStage.Advance();
                    return findings;
                })
                .ToList();
        }
        PhaseMemory.ReleaseBetweenPhases();

        List<ScalarUdfFinding> scalarUdfFindings;
        using (var scalarUdfStage = progress.Begin("scanning scalar UDFs", usableCount))
        {
            scalarUdfFindings = usableParseResults
                .AsParallel()
                .SelectMany(r =>
                {
                    var findings = ScalarUdfScanner.Scan(r, catalog, scalarUdfMap);
                    scalarUdfStage.Advance();
                    return findings;
                })
                .ToList();
        }
        PhaseMemory.ReleaseBetweenPhases();

        using (var schemaDependencyStage = progress.Begin("scanning schema-level scalar UDF dependencies"))
        {
            var schemaDependencyFindings = SchemaDependencyScanner.Scan(catalog);
            scalarUdfFindings.AddRange(schemaDependencyFindings);
            schemaDependencyStage.Complete($"{schemaDependencyFindings.Count:N0} findings");
        }

        IReadOnlyList<ColumnCollationDriftFinding> columnCollationDriftFindings;
        using (var collationDriftStage = progress.Begin("scanning column collation drift"))
        {
            columnCollationDriftFindings = ColumnCollationDriftScanner.Scan(catalog);
            collationDriftStage.Complete($"{columnCollationDriftFindings.Count:N0} findings");
        }

        IReadOnlyList<CrossTableTypeDriftFinding> crossTableTypeDriftFindings;
        using (var crossTableDriftStage = progress.Begin("scanning cross-table FK type drift"))
        {
            crossTableTypeDriftFindings = CrossTableTypeDriftScanner.Scan(catalog);
            crossTableDriftStage.Complete($"{crossTableTypeDriftFindings.Count:N0} findings");
        }

        IReadOnlyList<ProcCallArgumentMismatchFinding> procCallArgumentMismatchFindings;
        using (var argumentMismatchStage = progress.Begin("scanning call-boundary argument mismatches"))
        {
            procCallArgumentMismatchFindings = ProcCallArgumentMismatchScanner.Scan(procCallGraph);
            argumentMismatchStage.Complete($"{procCallArgumentMismatchFindings.Count:N0} findings");
        }

        IReadOnlyList<MaxTypedColumnFinding> maxTypedColumnFindings;
        using (var maxTypedColumnStage = progress.Begin("scanning MAX-typed columns"))
        {
            maxTypedColumnFindings = MaxTypedColumnScanner.Scan(catalog);
            maxTypedColumnStage.Complete($"{maxTypedColumnFindings.Count:N0} findings");
        }

        IReadOnlyList<NonPersistedComputedColumnFinding> nonPersistedComputedColumnFindings;
        using (var nonPersistedComputedColumnStage = progress.Begin("scanning non-persisted computed columns"))
        {
            nonPersistedComputedColumnFindings = NonPersistedComputedColumnScanner.Scan(catalog);
            nonPersistedComputedColumnStage.Complete($"{nonPersistedComputedColumnFindings.Count:N0} findings");
        }

        IReadOnlyList<UntrustedConstraintFinding> untrustedConstraintFindings;
        using (var untrustedConstraintStage = progress.Begin("scanning untrusted FK/CHECK constraints"))
        {
            untrustedConstraintFindings = UntrustedConstraintScanner.Scan(catalog);
            untrustedConstraintStage.Complete($"{untrustedConstraintFindings.Count:N0} findings");
        }

        IReadOnlyList<CheckConstraintFinding> checkConstraintFindings;
        using (var checkConstraintStage = progress.Begin("scanning CHECK constraint text"))
        {
            checkConstraintFindings = CheckConstraintScanner.Scan(catalog);
            checkConstraintStage.Complete($"{checkConstraintFindings.Count:N0} findings");
        }

        IReadOnlyList<SecurityPredicateIndexFinding> securityPredicateIndexFindings;
        using (var securityPredicateIndexStage = progress.Begin("scanning RLS predicate index coverage"))
        {
            securityPredicateIndexFindings = SecurityPredicateIndexScanner.Scan(catalog);
            securityPredicateIndexStage.Complete($"{securityPredicateIndexFindings.Count:N0} findings");
        }

        IReadOnlyList<DefaultNullableConstraintFinding> defaultNullableConstraintFindings;
        using (var defaultNullableStage = progress.Begin("scanning nullable DEFAULT constraints"))
        {
            defaultNullableConstraintFindings = DefaultNullableConstraintScanner.Scan(catalog);
            defaultNullableStage.Complete($"{defaultNullableConstraintFindings.Count:N0} findings");
        }

        var tryCastComputedColumnCandidates = TryCastComputedColumnPredicateScanner.BuildCandidates(catalog);

        IReadOnlyList<CascadingForeignKeyFinding> cascadingForeignKeyFindings;
        using (var cascadingFkStage = progress.Begin("scanning cascading FK actions"))
        {
            cascadingForeignKeyFindings = CascadingForeignKeyScanner.Scan(catalog);
            cascadingFkStage.Complete($"{cascadingForeignKeyFindings.Count:N0} findings");
        }

        IReadOnlyList<TemporalTableHistoryIndexGapFinding> temporalTableHistoryIndexGapFindings;
        using (var temporalHistoryStage = progress.Begin("scanning temporal table history-side index gaps"))
        {
            temporalTableHistoryIndexGapFindings = TemporalTableHistoryIndexGapScanner.Scan(catalog);
            temporalHistoryStage.Complete($"{temporalTableHistoryIndexGapFindings.Count:N0} findings");
        }

        List<PartialCompositeForeignKeyJoinFinding> partialCompositeForeignKeyJoinFindings;
        using (var partialFkJoinStage = progress.Begin("scanning partial composite-FK joins", usableCount))
        {
            var compositeForeignKeys = PartialCompositeForeignKeyJoinScanner.BuildCompositeForeignKeys(catalog);
            var unordered = usableParseResults
                .AsParallel()
                .SelectMany(r =>
                {
                    var findings = PartialCompositeForeignKeyJoinScanner.Scan(r, catalog, compositeForeignKeys);
                    partialFkJoinStage.Advance();
                    return findings;
                })
                .ToList();
            partialCompositeForeignKeyJoinFindings = unordered
                .OrderBy(f => f.SourcePath, StringComparer.Ordinal).ThenBy(f => f.Line).ThenBy(f => f.Column)
                .ToList();
        }
        PhaseMemory.ReleaseBetweenPhases();

        List<SetOptionFinding> setOptionFindings;
        using (var setOptionStage = progress.Begin("scanning SET options blocking indexed views/filtered indexes", usableCount))
        {
            var unordered = usableParseResults
                .AsParallel()
                .SelectMany(r =>
                {
                    var findings = SetOptionScanner.Scan(r, catalog, lineage);
                    setOptionStage.Advance();
                    return findings;
                })
                .ToList();
            setOptionFindings = unordered
                .OrderBy(f => f.Kind).ThenBy(f => f.SourcePath, StringComparer.Ordinal).ThenBy(f => f.Line).ThenBy(f => f.Column)
                .ToList();
        }
        PhaseMemory.ReleaseBetweenPhases();

        List<ModuleCompileFlagFinding> moduleCompileFlagFindings;
        using (var moduleCompileFlagStage = progress.Begin("scanning module compile flags (WITH RECOMPILE, database-collation-dependent TVF returns)", usableCount))
        {
            var unordered = usableParseResults
                .AsParallel()
                .SelectMany(r =>
                {
                    var findings = ModuleCompileFlagScanner.Scan(r, catalog);
                    moduleCompileFlagStage.Advance();
                    return findings;
                })
                .ToList();
            moduleCompileFlagFindings = unordered
                .OrderBy(f => f.Kind).ThenBy(f => f.SourcePath, StringComparer.Ordinal).ThenBy(f => f.Line).ThenBy(f => f.Column)
                .ToList();
        }
        PhaseMemory.ReleaseBetweenPhases();

        List<WindowFrameFinding> windowFrameFindings;
        using (var windowFrameStage = progress.Begin("scanning RANGE window-function frames", usableCount))
        {
            var unordered = usableParseResults
                .AsParallel()
                .SelectMany(r =>
                {
                    var findings = WindowFrameScanner.Scan(r);
                    windowFrameStage.Advance();
                    return findings;
                })
                .ToList();
            windowFrameFindings = unordered
                .OrderBy(f => f.Kind).ThenBy(f => f.SourcePath, StringComparer.Ordinal).ThenBy(f => f.Line).ThenBy(f => f.Column)
                .ToList();
        }
        PhaseMemory.ReleaseBetweenPhases();

        List<WaitForFinding> waitForFindings;
        using (var waitForStage = progress.Begin("scanning WAITFOR DELAY/TIME", usableCount))
        {
            var unordered = usableParseResults
                .AsParallel()
                .SelectMany(r =>
                {
                    var findings = WaitForScanner.Scan(r);
                    waitForStage.Advance();
                    return findings;
                })
                .ToList();
            waitForFindings = unordered
                .OrderBy(f => f.SourcePath, StringComparer.Ordinal).ThenBy(f => f.Line).ThenBy(f => f.Column)
                .ToList();
        }
        PhaseMemory.ReleaseBetweenPhases();

        List<ViewOrderingFinding> viewOrderingFindings;
        using (var viewOrderingStage = progress.Begin("scanning TOP(100) PERCENT/ORDER BY in views and inline TVFs", usableCount))
        {
            var unordered = usableParseResults
                .AsParallel()
                .SelectMany(r =>
                {
                    var findings = ViewOrderingScanner.Scan(r);
                    viewOrderingStage.Advance();
                    return findings;
                })
                .ToList();
            viewOrderingFindings = unordered
                .OrderBy(f => f.Kind).ThenBy(f => f.SourcePath, StringComparer.Ordinal).ThenBy(f => f.Line).ThenBy(f => f.Column)
                .ToList();
        }
        PhaseMemory.ReleaseBetweenPhases();

        List<TransactionHygieneFinding> transactionHygieneFindings;
        using (var transactionHygieneStage = progress.Begin("scanning transaction hygiene (unresolved BEGIN TRANSACTION)", usableCount))
        {
            var unordered = usableParseResults
                .AsParallel()
                .SelectMany(r =>
                {
                    var findings = TransactionHygieneScanner.Scan(r);
                    transactionHygieneStage.Advance();
                    return findings;
                })
                .ToList();
            transactionHygieneFindings = unordered
                .OrderBy(f => f.SourcePath, StringComparer.Ordinal).ThenBy(f => f.BeginTransactionLine).ThenBy(f => f.BeginTransactionColumn)
                .ToList();
        }
        PhaseMemory.ReleaseBetweenPhases();

        List<CompositeIndexLeadingColumnFinding> compositeIndexLeadingColumnFindings;
        using (var compositeIndexStage = progress.Begin("scanning composite index leading-column violations", usableCount))
        {
            var unordered = usableParseResults
                .AsParallel()
                .SelectMany(r =>
                {
                    var findings = CompositeIndexLeadingColumnScanner.Scan(r, catalog);
                    compositeIndexStage.Advance();
                    return findings;
                })
                .ToList();
            compositeIndexLeadingColumnFindings = unordered
                .OrderBy(f => f.SourcePath, StringComparer.Ordinal).ThenBy(f => f.Line).ThenBy(f => f.Column)
                .ToList();
        }
        PhaseMemory.ReleaseBetweenPhases();

        List<IndexHintFinding> indexHintFindings;
        using (var indexHintStage = progress.Begin("scanning INDEX hint validity", usableCount))
        {
            var unordered = usableParseResults
                .AsParallel()
                .SelectMany(r =>
                {
                    var findings = IndexHintScanner.Scan(r, catalog);
                    indexHintStage.Advance();
                    return findings;
                })
                .ToList();
            indexHintFindings = unordered
                .OrderBy(f => f.Kind).ThenBy(f => f.SourcePath, StringComparer.Ordinal).ThenBy(f => f.Line).ThenBy(f => f.Column)
                .ToList();
        }
        PhaseMemory.ReleaseBetweenPhases();

        List<SessionDateSettingFinding> sessionDateSettingFindings;
        using (var sessionDateStage = progress.Begin("scanning SET DATEFORMAT/DATEFIRST", usableCount))
        {
            var unordered = usableParseResults
                .AsParallel()
                .SelectMany(r =>
                {
                    var findings = SessionDateSettingScanner.Scan(r);
                    sessionDateStage.Advance();
                    return findings;
                })
                .ToList();
            sessionDateSettingFindings = unordered
                .OrderBy(f => f.SourcePath, StringComparer.Ordinal).ThenBy(f => f.Line).ThenBy(f => f.Column)
                .ToList();
        }
        PhaseMemory.ReleaseBetweenPhases();

        List<CartesianJoinFinding> cartesianJoinFindings;
        using (var cartesianJoinStage = progress.Begin("scanning true cartesian joins", usableCount))
        {
            var unordered = usableParseResults
                .AsParallel()
                .SelectMany(r =>
                {
                    var findings = CartesianJoinScanner.Scan(r);
                    cartesianJoinStage.Advance();
                    return findings;
                })
                .ToList();
            cartesianJoinFindings = unordered
                .OrderBy(f => f.SourcePath, StringComparer.Ordinal).ThenBy(f => f.Line).ThenBy(f => f.Column)
                .ToList();
        }
        PhaseMemory.ReleaseBetweenPhases();

        List<UndersizedDeclarationFinding> undersizedDeclarationFindings;
        using (var undersizedDeclarationStage = progress.Begin("scanning undersized (length 1/2) declarations", usableCount))
        {
            var unordered = usableParseResults
                .AsParallel()
                .SelectMany(r =>
                {
                    var findings = UndersizedDeclarationScanner.ScanDeclarations(r, catalog);
                    undersizedDeclarationStage.Advance();
                    return findings;
                })
                .ToList();
            unordered.AddRange(UndersizedDeclarationScanner.ScanCatalog(catalog));
            undersizedDeclarationFindings = unordered
                .OrderBy(f => f.QualifiedOrVariableName, StringComparer.Ordinal).ThenBy(f => f.SourcePath, StringComparer.Ordinal).ThenBy(f => f.Line)
                .ToList();
        }
        PhaseMemory.ReleaseBetweenPhases();

        List<TruncateSwallowedFinding> truncateSwallowedFindings;
        using (var truncateSwallowedStage = progress.Begin("scanning TRUNCATE inside a swallowing TRY/CATCH", usableCount))
        {
            var unordered = usableParseResults
                .AsParallel()
                .SelectMany(r =>
                {
                    var findings = TruncateSwallowedScanner.Scan(r);
                    truncateSwallowedStage.Advance();
                    return findings;
                })
                .ToList();
            truncateSwallowedFindings = unordered
                .OrderBy(f => f.SourcePath, StringComparer.Ordinal).ThenBy(f => f.Line).ThenBy(f => f.Column)
                .ToList();
        }
        PhaseMemory.ReleaseBetweenPhases();

        List<UnindexedTempTableUsageFinding> unindexedTempTableUsageFindings;
        using (var unindexedTempTableStage = progress.Begin("scanning unindexed SELECT INTO temp tables", usableCount))
        {
            var unordered = usableParseResults
                .AsParallel()
                .SelectMany(r =>
                {
                    var findings = UnindexedTempTableUsageScanner.Scan(r, catalog);
                    unindexedTempTableStage.Advance();
                    return findings;
                })
                .ToList();
            unindexedTempTableUsageFindings = unordered
                .OrderBy(f => f.SourcePath, StringComparer.Ordinal).ThenBy(f => f.DeclarationLine)
                .ToList();
        }
        PhaseMemory.ReleaseBetweenPhases();

        List<OutputParameterFinding> outputParameterFindings;
        using (var outputParameterStage = progress.Begin("scanning unassigned OUTPUT parameters", usableCount))
        {
            var unordered = usableParseResults
                .AsParallel()
                .SelectMany(r =>
                {
                    var findings = OutputParameterScanner.Scan(r);
                    outputParameterStage.Advance();
                    return findings;
                })
                .ToList();
            outputParameterFindings = unordered
                .OrderBy(f => f.SourcePath, StringComparer.Ordinal).ThenBy(f => f.ProcedureLine)
                .ThenBy(f => f.ParameterName, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }
        PhaseMemory.ReleaseBetweenPhases();

        List<CatchAllPredicateFinding> catchAllPredicateFindings;
        using (var catchAllStage = progress.Begin("scanning catch-all predicates", usableCount))
        {
            var unordered = usableParseResults
                .AsParallel()
                .SelectMany(r =>
                {
                    var findings = CatchAllPredicateScanner.Scan(r, catalog);
                    catchAllStage.Advance();
                    return findings;
                })
                .ToList();
            catchAllPredicateFindings = unordered
                .OrderBy(f => f.SourcePath, StringComparer.Ordinal).ThenBy(f => f.Line).ThenBy(f => f.Column)
                .ToList();
        }
        PhaseMemory.ReleaseBetweenPhases();

        List<TryCastComputedColumnPredicateFinding> tryCastComputedColumnPredicateFindings;
        using (var tryCastStage = progress.Begin("scanning TRY_CAST computed columns used in predicates", usableCount))
        {
            var unordered = tryCastComputedColumnCandidates.Count == 0
                ? []
                : usableParseResults
                    .AsParallel()
                    .SelectMany(r =>
                    {
                        var findings = TryCastComputedColumnPredicateScanner.Scan(r, catalog, tryCastComputedColumnCandidates);
                        tryCastStage.Advance();
                        return findings;
                    })
                    .ToList();
            tryCastComputedColumnPredicateFindings = unordered
                .OrderBy(f => f.TableQualifiedName, StringComparer.Ordinal).ThenBy(f => f.ColumnName, StringComparer.Ordinal)
                .ThenBy(f => f.PredicateSourcePath, StringComparer.Ordinal).ThenBy(f => f.PredicateLine)
                .ToList();
            tryCastStage.Complete($"{tryCastComputedColumnPredicateFindings.Count:N0} findings");
        }
        PhaseMemory.ReleaseBetweenPhases();

        List<BareTopNoOrderByFinding> bareTopNoOrderByFindings;
        using (var bareTopStage = progress.Begin("scanning bare TOP with no ORDER BY", usableCount))
        {
            var unordered = usableParseResults
                .AsParallel()
                .SelectMany(r =>
                {
                    var findings = BareTopNoOrderByScanner.Scan(r);
                    bareTopStage.Advance();
                    return findings;
                })
                .ToList();
            bareTopNoOrderByFindings = unordered
                .OrderBy(f => f.SourcePath, StringComparer.Ordinal).ThenBy(f => f.Line).ThenBy(f => f.Column)
                .ToList();
            bareTopStage.Complete($"{bareTopNoOrderByFindings.Count:N0} findings");
        }
        PhaseMemory.ReleaseBetweenPhases();

        List<StringConcatNullFinding> stringConcatNullFindings;
        using (var stringConcatStage = progress.Begin("scanning + operator string concatenation NULL propagation", usableCount))
        {
            var unordered = usableParseResults
                .AsParallel()
                .SelectMany(r =>
                {
                    var findings = StringConcatNullScanner.Scan(r, catalog);
                    stringConcatStage.Advance();
                    return findings;
                })
                .ToList();
            stringConcatNullFindings = unordered
                .OrderBy(f => f.SourcePath, StringComparer.Ordinal).ThenBy(f => f.Line).ThenBy(f => f.Column)
                .ToList();
            stringConcatStage.Complete($"{stringConcatNullFindings.Count:N0} findings");
        }
        PhaseMemory.ReleaseBetweenPhases();

        List<AggregateDivisionColumnstoreFinding> aggregateDivisionColumnstoreFindings;
        using (var aggregateDivisionStage = progress.Begin("scanning CASE-guarded division inside aggregates on columnstore tables", usableCount))
        {
            var unordered = usableParseResults
                .AsParallel()
                .SelectMany(r =>
                {
                    var findings = AggregateDivisionColumnstoreScanner.Scan(r, catalog);
                    aggregateDivisionStage.Advance();
                    return findings;
                })
                .ToList();
            aggregateDivisionColumnstoreFindings = unordered
                .OrderBy(f => f.SourcePath, StringComparer.Ordinal).ThenBy(f => f.Line).ThenBy(f => f.Column)
                .ToList();
            aggregateDivisionStage.Complete($"{aggregateDivisionColumnstoreFindings.Count:N0} findings");
        }
        PhaseMemory.ReleaseBetweenPhases();

        List<ParameterReassignmentPredicateFinding> parameterReassignmentPredicateFindings;
        using (var reassignmentStage = progress.Begin("scanning reassigned-parameter predicates", usableCount))
        {
            var unordered = usableParseResults
                .AsParallel()
                .SelectMany(r =>
                {
                    var findings = ParameterReassignmentPredicateScanner.Scan(r, catalog);
                    reassignmentStage.Advance();
                    return findings;
                })
                .ToList();
            parameterReassignmentPredicateFindings = unordered
                .OrderBy(f => f.SourcePath, StringComparer.Ordinal).ThenBy(f => f.Line).ThenBy(f => f.Column)
                .ToList();
        }
        PhaseMemory.ReleaseBetweenPhases();

        List<CodeMetricFinding> codeMetricFindings;
        using (var codeMetricStage = progress.Begin("scanning size/complexity metrics", usableCount))
        {
            var unordered = usableParseResults
                .AsParallel()
                .SelectMany(r =>
                {
                    var findings = CodeMetricScanner.Scan(r);
                    codeMetricStage.Advance();
                    return findings;
                })
                .ToList();
            codeMetricFindings = unordered
                .OrderBy(f => f.Kind).ThenBy(f => f.SourcePath, StringComparer.Ordinal).ThenBy(f => f.Line).ThenBy(f => f.Column)
                .ToList();
        }
        PhaseMemory.ReleaseBetweenPhases();

        List<FormattingFinding> formattingFindings;
        using (var formattingStage = progress.Begin("scanning formatting and layout", usableCount))
        {
            var unordered = usableParseResults
                .AsParallel()
                .SelectMany(r =>
                {
                    var findings = FormattingScanner.Scan(r);
                    formattingStage.Advance();
                    return findings;
                })
                .ToList();
            formattingFindings = unordered
                .OrderBy(f => f.Kind).ThenBy(f => f.SourcePath, StringComparer.Ordinal).ThenBy(f => f.Line).ThenBy(f => f.Column)
                .ToList();
        }
        PhaseMemory.ReleaseBetweenPhases();

        List<NamingFinding> namingFindings;
        using (var namingStage = progress.Begin("scanning naming and identifier risks", usableCount))
        {
            var unordered = usableParseResults
                .AsParallel()
                .SelectMany(r =>
                {
                    var findings = NamingScanner.Scan(r);
                    namingStage.Advance();
                    return findings;
                })
                .ToList();
            namingFindings = unordered
                .OrderBy(f => f.Kind).ThenBy(f => f.SourcePath, StringComparer.Ordinal).ThenBy(f => f.Line).ThenBy(f => f.Column)
                .ToList();
        }
        PhaseMemory.ReleaseBetweenPhases();

        List<DeadCodeFinding> deadCodeFindings;
        using (var deadCodeStage = progress.Begin("scanning dead code and control-flow risks", usableCount))
        {
            var unordered = usableParseResults
                .AsParallel()
                .SelectMany(r =>
                {
                    var findings = DeadCodeScanner.Scan(r);
                    deadCodeStage.Advance();
                    return findings;
                })
                .ToList();
            deadCodeFindings = unordered
                .OrderBy(f => f.Kind).ThenBy(f => f.SourcePath, StringComparer.Ordinal).ThenBy(f => f.Line).ThenBy(f => f.Column)
                .ToList();
        }
        PhaseMemory.ReleaseBetweenPhases();

        List<DuplicationFinding> duplicationFindings;
        using (var duplicationStage = progress.Begin("scanning duplicated/redundant code shapes", usableCount))
        {
            var unordered = usableParseResults
                .AsParallel()
                .SelectMany(r =>
                {
                    var findings = DuplicationScanner.Scan(r);
                    duplicationStage.Advance();
                    return findings;
                })
                .ToList();
            duplicationFindings = unordered
                .OrderBy(f => f.Kind).ThenBy(f => f.SourcePath, StringComparer.Ordinal).ThenBy(f => f.Line).ThenBy(f => f.Column)
                .ToList();
        }
        PhaseMemory.ReleaseBetweenPhases();

        List<DeprecatedSyntaxFinding> deprecatedSyntaxFindings;
        using (var deprecatedSyntaxStage = progress.Begin("scanning task comments and deprecated syntax", usableCount))
        {
            var unordered = usableParseResults
                .AsParallel()
                .SelectMany(r =>
                {
                    var findings = DeprecatedSyntaxScanner.Scan(r);
                    deprecatedSyntaxStage.Advance();
                    return findings;
                })
                .ToList();
            deprecatedSyntaxFindings = unordered
                .OrderBy(f => f.Kind).ThenBy(f => f.SourcePath, StringComparer.Ordinal).ThenBy(f => f.Line).ThenBy(f => f.Column)
                .ToList();
        }
        PhaseMemory.ReleaseBetweenPhases();

        List<StatementShapeFinding> statementShapeFindings;
        using (var statementShapeStage = progress.Begin("scanning statement-shape risks", usableCount))
        {
            var unordered = usableParseResults
                .AsParallel()
                .SelectMany(r =>
                {
                    var findings = StatementShapeScanner.Scan(r);
                    statementShapeStage.Advance();
                    return findings;
                })
                .ToList();
            unordered.AddRange(StatementShapeScanner.ScanCatalog(catalog));
            statementShapeFindings = unordered
                .OrderBy(f => f.Kind).ThenBy(f => f.SourcePath, StringComparer.Ordinal).ThenBy(f => f.Line).ThenBy(f => f.Column)
                .ToList();
        }
        PhaseMemory.ReleaseBetweenPhases();

        List<ControlFlowRiskFinding> controlFlowRiskFindings;
        using (var controlFlowRiskStage = progress.Begin("scanning cursor and control-flow risks", usableCount))
        {
            var unordered = usableParseResults
                .AsParallel()
                .SelectMany(r =>
                {
                    var findings = ControlFlowRiskScanner.Scan(r);
                    controlFlowRiskStage.Advance();
                    return findings;
                })
                .ToList();
            controlFlowRiskFindings = unordered
                .OrderBy(f => f.Kind).ThenBy(f => f.SourcePath, StringComparer.Ordinal).ThenBy(f => f.Line).ThenBy(f => f.Column)
                .ToList();
        }
        PhaseMemory.ReleaseBetweenPhases();

        List<SecurityFinding> securityFindings;
        using (var securityStage = progress.Begin("scanning security risks", usableCount))
        {
            var unordered = usableParseResults
                .AsParallel()
                .SelectMany(r =>
                {
                    var findings = SecurityScanner.Scan(r);
                    securityStage.Advance();
                    return findings;
                })
                .ToList();
            securityFindings = unordered
                .OrderBy(f => f.Kind).ThenBy(f => f.SourcePath, StringComparer.Ordinal).ThenBy(f => f.Line).ThenBy(f => f.Column)
                .ToList();
        }
        PhaseMemory.ReleaseBetweenPhases();

        List<NotInNullableSubqueryFinding> notInNullableSubqueryFindings;
        using (var notInStage = progress.Begin("scanning NOT IN over nullable subquery columns", usableCount))
        {
            var unordered = usableParseResults
                .AsParallel()
                .SelectMany(r =>
                {
                    var findings = NotInNullableSubqueryScanner.Scan(r, catalog);
                    notInStage.Advance();
                    return findings;
                })
                .ToList();
            notInNullableSubqueryFindings = unordered
                .OrderBy(f => f.SourcePath, StringComparer.Ordinal).ThenBy(f => f.Line).ThenBy(f => f.Column)
                .ToList();
        }
        PhaseMemory.ReleaseBetweenPhases();

        List<NonUniqueUpdateSourceFinding> nonUniqueUpdateSourceFindings;
        using (var updateSourceStage = progress.Begin("scanning UPDATE...FROM source uniqueness", usableCount))
        {
            var unordered = usableParseResults
                .AsParallel()
                .SelectMany(r =>
                {
                    var findings = NonUniqueUpdateSourceScanner.Scan(r, catalog);
                    updateSourceStage.Advance();
                    return findings;
                })
                .ToList();
            nonUniqueUpdateSourceFindings = unordered
                .OrderBy(f => f.SourcePath, StringComparer.Ordinal).ThenBy(f => f.Line).ThenBy(f => f.Column)
                .ToList();
        }
        PhaseMemory.ReleaseBetweenPhases();

        List<FloatEqualityFinding> floatEqualityFindings;
        using (var floatEqualityStage = progress.Begin("scanning float/real equality predicates", usableCount))
        {
            var unordered = usableParseResults
                .AsParallel()
                .SelectMany(r =>
                {
                    var findings = FloatEqualityPredicateScanner.Scan(r, catalog);
                    floatEqualityStage.Advance();
                    return findings;
                })
                .ToList();
            floatEqualityFindings = unordered
                .OrderBy(f => f.SourcePath, StringComparer.Ordinal).ThenBy(f => f.Line).ThenBy(f => f.Column)
                .ToList();
        }
        PhaseMemory.ReleaseBetweenPhases();

        List<QueryAntiPatternFinding> queryAntiPatternFindings;
        using (var queryAntiPatternStage = progress.Begin("scanning query anti-patterns", usableCount))
        {
            var unordered = usableParseResults
                .AsParallel()
                .SelectMany(r =>
                {
                    var findings = QueryAntiPatternScanner.Scan(r, catalog);
                    queryAntiPatternStage.Advance();
                    return findings;
                })
                .ToList();
            queryAntiPatternFindings = unordered
                .OrderBy(f => f.Kind).ThenBy(f => f.SourcePath, StringComparer.Ordinal).ThenBy(f => f.Line).ThenBy(f => f.Column)
                .ToList();
        }
        PhaseMemory.ReleaseBetweenPhases();

        List<IndexCoverageFinding> indexCoverageFindings;
        using (var indexCoverageStage = progress.Begin("scanning index-coverage shapes", usableCount))
        {
            var unordered = usableParseResults
                .AsParallel()
                .SelectMany(r =>
                {
                    var findings = IndexCoverageScanner.Scan(r, catalog);
                    indexCoverageStage.Advance();
                    return findings;
                })
                .ToList();
            indexCoverageFindings = unordered
                .OrderBy(f => f.SourcePath, StringComparer.Ordinal).ThenBy(f => f.Line).ThenBy(f => f.Column)
                .ToList();
        }
        PhaseMemory.ReleaseBetweenPhases();

        List<TriggerCorrectnessFinding> triggerCorrectnessFindings;
        using (var triggerCorrectnessStage = progress.Begin("scanning trigger correctness", usableCount))
        {
            var unordered = usableParseResults
                .AsParallel()
                .SelectMany(r =>
                {
                    var findings = TriggerCorrectnessScanner.Scan(r, catalog);
                    triggerCorrectnessStage.Advance();
                    return findings;
                })
                .ToList();
            triggerCorrectnessFindings = unordered
                .OrderBy(f => f.Kind).ThenBy(f => f.SourcePath, StringComparer.Ordinal).ThenBy(f => f.Line).ThenBy(f => f.Column)
                .ToList();
        }
        PhaseMemory.ReleaseBetweenPhases();

        IReadOnlyList<CrossModuleLockOrderFinding> crossModuleLockOrderFindings;
        using (var lockOrderStage = progress.Begin("scanning cross-module lock ordering"))
        {
            // A whole-scan pass, not per-file - the same table pair must be seen written in
            // opposite order by two DIFFERENT top-level procedures, which can live in different
            // files, so this needs every usable parse result together rather than one file at a
            // time the way every SelectMany-based stage above works.
            crossModuleLockOrderFindings = CrossModuleLockOrderScanner.Scan(usableParseResults, catalog);
            lockOrderStage.Complete($"{crossModuleLockOrderFindings.Count:N0} findings");
        }
        PhaseMemory.ReleaseBetweenPhases();

        IReadOnlyList<TriggerRecursionCycleFinding> triggerRecursionCycleFindings;
        using (var triggerRecursionStage = progress.Begin("scanning multi-hop trigger recursion cycles"))
        {
            // Same "whole-scan, not per-file" reasoning as CrossModuleLockOrderScanner just above -
            // a genuine two-table cycle's two triggers routinely live in different files.
            triggerRecursionCycleFindings = TriggerRecursionCycleScanner.Scan(usableParseResults, catalog);
            triggerRecursionStage.Complete($"{triggerRecursionCycleFindings.Count:N0} findings");
        }
        PhaseMemory.ReleaseBetweenPhases();

        List<ForcedSerialFinding> forcedSerialFindings;
        using (var forcedSerialStage = progress.Begin("scanning forced-serial constructs", usableCount))
        {
            var unordered = usableParseResults
                .AsParallel()
                .SelectMany(r =>
                {
                    var findings = ForcedSerialScanner.Scan(r);
                    forcedSerialStage.Advance();
                    return findings;
                })
                .ToList();
            forcedSerialFindings = unordered
                .OrderBy(f => f.Kind).ThenBy(f => f.SourcePath, StringComparer.Ordinal).ThenBy(f => f.Line).ThenBy(f => f.Column)
                .ToList();
        }
        PhaseMemory.ReleaseBetweenPhases();

        List<MultiReferencedCteFinding> multiReferencedCteFindings;
        using (var multiCteStage = progress.Begin("scanning multi-referenced CTEs", usableCount))
        {
            var unordered = usableParseResults
                .AsParallel()
                .SelectMany(r =>
                {
                    var findings = MultiReferencedCteScanner.Scan(r);
                    multiCteStage.Advance();
                    return findings;
                })
                .ToList();
            multiReferencedCteFindings = unordered
                .OrderBy(f => f.SourcePath, StringComparer.Ordinal).ThenBy(f => f.Line)
                .ToList();
        }
        PhaseMemory.ReleaseBetweenPhases();

        IReadOnlyList<NestedViewDepthFinding> nestedViewDepthFindings;
        using (var nestedViewDepthStage = progress.Begin("scanning nested-view depth"))
        {
            nestedViewDepthFindings = NestedViewDepthScanner.Scan(viewExpansionMap, viewDefinitions);
            nestedViewDepthStage.Complete($"{nestedViewDepthFindings.Count:N0} findings");
        }

        List<PostExpansionJoinWidthFinding> postExpansionJoinWidthFindings;
        using (var joinWidthStage = progress.Begin("scanning post-expansion join width", usableCount))
        {
            var unordered = usableParseResults
                .AsParallel()
                .SelectMany(r =>
                {
                    var findings = PostExpansionJoinWidthScanner.Scan(r, catalog, viewExpansionMap);
                    joinWidthStage.Advance();
                    return findings;
                })
                .ToList();
            postExpansionJoinWidthFindings = unordered
                .OrderByDescending(f => f.ExpandedCount - f.WrittenCount)
                .ThenBy(f => f.SourcePath, StringComparer.Ordinal)
                .ThenBy(f => f.Line)
                .ToList();
        }
        PhaseMemory.ReleaseBetweenPhases();

        List<SelfReferencingDmlFinding> selfReferencingDmlFindings;
        using (var selfRefStage = progress.Begin("scanning self-referencing DML", usableCount))
        {
            var unordered = usableParseResults
                .AsParallel()
                .SelectMany(r =>
                {
                    var findings = SelfReferencingDmlScanner.Scan(r, catalog, viewExpansionMap);
                    selfRefStage.Advance();
                    return findings;
                })
                .ToList();
            selfReferencingDmlFindings = unordered
                .OrderBy(f => f.SourcePath, StringComparer.Ordinal)
                .ThenBy(f => f.Line)
                .ThenBy(f => f.Column)
                .ToList();
        }
        PhaseMemory.ReleaseBetweenPhases();

        List<SelectStarViewFinding> selectStarViewFindings;
        using (var selectStarStage = progress.Begin("scanning SELECT * inside nested views", usableCount))
        {
            var unordered = usableParseResults
                .AsParallel()
                .SelectMany(r =>
                {
                    var findings = SelectStarViewScanner.Scan(r, catalog, lineage, selectStarViewCandidates);
                    selectStarStage.Advance();
                    return findings;
                })
                .ToList();
            selectStarViewFindings = unordered
                .OrderBy(f => f.ViewQualifiedName, StringComparer.Ordinal)
                .ThenBy(f => f.ConsumerSourcePath, StringComparer.Ordinal)
                .ThenBy(f => f.ConsumerLine)
                .ToList();
        }
        PhaseMemory.ReleaseBetweenPhases();

        List<PredicateExtractionResult> extractionResults;
        using (var typedStage = progress.Begin("scanning typed predicates", usableCount))
        {
            extractionResults = usableParseResults.AsParallel()
                .Select(r =>
                {
                    var extracted = TypedPredicateExtractor.Extract(r, catalog, lineage, callerScopeByCalleeScope: callerScopeByCalleeScope);
                    typedStage.Advance();
                    return extracted;
                })
                .ToList();
        }
        var typedFindings = extractionResults.SelectMany(r => r.TypedFindings).ToList();
        var expressionDerivedFindings = extractionResults.SelectMany(r => r.ExpressionDerivedFindings).ToList();
        var collationConflictFindings = extractionResults.SelectMany(r => r.CollationConflictFindings).ToList();
        var writeLossFindings = extractionResults.SelectMany(r => r.WriteLossFindings).ToList();
        var oversizedParameterFindings = extractionResults.SelectMany(r => r.OversizedParameterFindings)
            .OrderBy(f => f.SourcePath, StringComparer.Ordinal).ThenBy(f => f.Line).ThenBy(f => f.Column).ToList();
        var underLengthParameterFindings = extractionResults.SelectMany(r => r.UnderLengthParameterFindings)
            .OrderBy(f => f.SourcePath, StringComparer.Ordinal).ThenBy(f => f.Line).ThenBy(f => f.Column).ToList();
        var ansiPaddingMismatchFindings = extractionResults.SelectMany(r => r.AnsiPaddingMismatchFindings)
            .OrderBy(f => f.SourcePath, StringComparer.Ordinal).ThenBy(f => f.Line).ThenBy(f => f.Column).ToList();
        var localVariablePredicateFindings = extractionResults.SelectMany(r => r.LocalVariablePredicateFindings)
            .OrderBy(f => f.SourcePath, StringComparer.Ordinal).ThenBy(f => f.Line).ThenBy(f => f.Column).ToList();
        var filteredIndexParameterMismatchFindings = extractionResults.SelectMany(r => r.FilteredIndexParameterMismatchFindings)
            .OrderBy(f => f.SourcePath, StringComparer.Ordinal).ThenBy(f => f.Line).ThenBy(f => f.Column).ToList();
        PhaseMemory.ReleaseBetweenPhases();

        var skippedConstructs = new List<SkippedConstruct>();
        skippedConstructs.AddRange(catalog.Skipped.Entries);
        skippedConstructs.AddRange(lineage.Skipped.Entries);
        skippedConstructs.AddRange(callGraphLedger.Entries);
        skippedConstructs.AddRange(tier1SkippedEntries);
        skippedConstructs.AddRange(extractionResults.SelectMany(r => r.SkippedConstructs));

        // Tier A of the dynamic SQL policy (CLAUDE.md): reparse provably-constant EXEC/
        // sp_executesql arguments through the same pipeline and fold their findings in,
        // remapped back to their true source location.
        var dynamicSqlResult = DynamicSqlPipeline.Analyze(dynamicSqlScripts, catalog, lineage, tvfFenceMap, scalarUdfMap, callerScopeByCalleeScope);
        dynamicSqlFindings = [.. dynamicSqlFindings, .. dynamicSqlResult.Findings];
        tier1Findings = [.. tier1Findings, .. dynamicSqlResult.Tier1Findings];
        typedFindings = [.. typedFindings, .. dynamicSqlResult.TypedFindings];
        expressionDerivedFindings = [.. expressionDerivedFindings, .. dynamicSqlResult.ExpressionDerivedFindings];
        collationConflictFindings = [.. collationConflictFindings, .. dynamicSqlResult.CollationConflictFindings];
        writeLossFindings = [.. writeLossFindings, .. dynamicSqlResult.WriteLossFindings];
        tvfFenceFindings = [.. tvfFenceFindings, .. dynamicSqlResult.TvfFenceFindings];
        scalarUdfFindings = [.. scalarUdfFindings, .. dynamicSqlResult.ScalarUdfFindings];
        skippedConstructs.AddRange(dynamicSqlResult.SkippedConstructs);

        // docs/detection-checklist.md Tier 2 "Dynamic SQL quality" - purely a dynamic-SQL-pass
        // finding (a concatenation boundary only exists inside a folded EXEC/sp_executesql
        // assembly's own reparse), so unlike every other stream above it has no static-file half
        // to merge with - the dynamic SQL result IS the whole list.
        var unparameterizedDynamicSqlFindings = dynamicSqlResult.UnparameterizedFindings
            .Where(f => f.Confidence <= minimumConfidence)
            .OrderBy(f => f.SourcePath, StringComparer.Ordinal)
            .ThenBy(f => f.Line)
            .ThenBy(f => f.Kind)
            .ToList();

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

        typedFindings = [.. typedFindings.Where(f => f.Verdict != Verdict.SeekPreserved && f.Confidence <= minimumConfidence)];
        tier1Findings = [.. tier1Findings.Where(f => f.Confidence <= minimumConfidence)];
        expressionDerivedFindings = [.. expressionDerivedFindings.Where(f => f.Confidence <= minimumConfidence)];
        collationConflictFindings = [.. collationConflictFindings.Where(f => f.Confidence <= minimumConfidence)];
        writeLossFindings = [.. writeLossFindings.Where(f => f.Confidence <= minimumConfidence)];
        tvfFenceFindings = [.. tvfFenceFindings.Where(f => f.Confidence <= minimumConfidence)];
        scalarUdfFindings = [.. scalarUdfFindings.Where(f => f.Confidence <= minimumConfidence)];

        // Deterministic output ordering (CLAUDE.md), then CLAUDE.md's Pass 4 rank:
        // SCAN_FORCED + indexed + depth>=1 first. Index-existence weighting
        // (docs/detection-checklist.md Tier 1 "Type-aware upgrade of the sargability stream" #5)
        // mirrors TypedFindings' own ThenByDescending(f => f.Column.Indexed) below - a non-
        // sargable predicate on an unindexed column is noise, on an indexed column it's a real
        // lost seek. Indexed is nullable (unresolved != false, CLAUDE.md's own "never guess"
        // discipline) - true ranks first (a proven lost seek), unresolved ranks second (real
        // signal, just not confirmed), false ranks last (confirmed noise).
        tier1Findings = [.. tier1Findings
            .OrderBy(f => f.Indexed switch { true => 0, null => 1, false => 2 })
            .ThenBy(f => f.SourcePath, StringComparer.Ordinal)
            .ThenBy(f => f.Line)
            .ThenBy(f => f.Column)];
        dynamicSqlFindings = [.. dynamicSqlFindings.OrderBy(f => f.SourcePath, StringComparer.Ordinal).ThenBy(f => f.Line)];
        securityFindings = [.. securityFindings, .. SecurityScanner.FromDynamicSqlFindings(dynamicSqlFindings)];
        securityFindings = [.. securityFindings
            .OrderBy(f => f.Kind).ThenBy(f => f.SourcePath, StringComparer.Ordinal).ThenBy(f => f.Line).ThenBy(f => f.Column)];
        typedFindings = [.. typedFindings
            .OrderBy(f => VerdictRank(f.Verdict))
            .ThenByDescending(f => f.Column.Indexed)
            .ThenByDescending(f => f.Column.Depth)
            .ThenBy(f => f.SourcePath, StringComparer.Ordinal)
            .ThenBy(f => f.Line)];
        expressionDerivedFindings = [.. expressionDerivedFindings.OrderBy(f => f.SourcePath, StringComparer.Ordinal).ThenBy(f => f.Line)];
        collationConflictFindings = [.. collationConflictFindings.OrderBy(f => f.SourcePath, StringComparer.Ordinal).ThenBy(f => f.Line)];
        writeLossFindings = [.. writeLossFindings.OrderBy(f => f.SourcePath, StringComparer.Ordinal).ThenBy(f => f.Line)];

        // docs/detection-checklist.md's own rank for this stream: correlated APPLY first (no
        // engine version rescues it), then a fence inherited invisibly through a view/TVF layer
        // (ranked by how many layers deep - the number no text-matching tool can produce), then
        // a direct FROM/JOIN reference, then INSERT...EXEC, then a standalone reference last
        // (real, but nothing around it to poison) - exactly the declared order of
        // TvfFenceFindingKind's own enum members.
        tvfFenceFindings = [.. tvfFenceFindings
            .OrderBy(f => f.Kind)
            .ThenByDescending(f => f.Depth)
            .ThenBy(f => f.SourcePath, StringComparer.Ordinal)
            .ThenBy(f => f.Line)];

        // docs/detection-checklist.md's own rank for this stream: predicate-context invocation
        // first (the maximal claim - non-sargable AND per-row AND, pre-2019 or non-inlineable,
        // serial), then reached-through-lineage (depth is the number no text-matching tool can
        // produce), then a schema-level dependency, then a plain projection-context invocation
        // last - the declared order of ScalarUdfFindingKind's own enum members. Within a kind,
        // NotInlineable ranks above Unknown above Inlineable - the severity-downgrade guard
        // expressed as ordering, matching the checklist's "report inlined-in-2019+ cases at
        // reduced severity".
        scalarUdfFindings = [.. scalarUdfFindings
            .OrderBy(f => f.Kind)
            .ThenBy(f => InlineabilityRank(f.Inlineability))
            .ThenByDescending(f => f.Depth)
            .ThenBy(f => f.SourcePath, StringComparer.Ordinal)
            .ThenBy(f => f.Line)
            .ThenBy(f => f.Column)];
        var orderedSkippedConstructs = skippedConstructs
            .OrderBy(s => s.Pass)
            .ThenBy(s => s.SourcePath, StringComparer.Ordinal)
            .ThenBy(s => s.Line)
            .ThenBy(s => s.Column)
            .ToList();

        // Every stream landed after the original eight (filtered just above, lines ~1131-1137)
        // never got this same minimumConfidence cut applied - a real gap, not a deliberate
        // design choice: CLAUDE.md's own contract for this parameter is "the least confident a
        // finding may be and still appear in the report," and a Low/Medium-confidence finding
        // from any of these streams was silently bypassing that promise regardless of what the
        // caller (scan-db --confidence, scan-corpus --confidence) actually asked for. Fixed by
        // applying the identical `<= minimumConfidence` cut to every remaining stream, in one
        // place, right before construction, so a future new stream only has to be added to this
        // one block rather than remembering to filter itself inline the way each new stream's
        // own scan phase above never did.
        columnCollationDriftFindings = [.. columnCollationDriftFindings.Where(f => f.Confidence <= minimumConfidence)];
        crossTableTypeDriftFindings = [.. crossTableTypeDriftFindings.Where(f => f.Confidence <= minimumConfidence)];
        procCallArgumentMismatchFindings = [.. procCallArgumentMismatchFindings.Where(f => f.Confidence <= minimumConfidence)];
        temporalBoundaryFindings = [.. temporalBoundaryFindings.Where(f => f.Confidence <= minimumConfidence)];
        maxTypedColumnFindings = [.. maxTypedColumnFindings.Where(f => f.Confidence <= minimumConfidence)];
        oversizedParameterFindings = [.. oversizedParameterFindings.Where(f => f.Confidence <= minimumConfidence)];
        underLengthParameterFindings = [.. underLengthParameterFindings.Where(f => f.Confidence <= minimumConfidence)];
        ansiPaddingMismatchFindings = [.. ansiPaddingMismatchFindings.Where(f => f.Confidence <= minimumConfidence)];
        partialCompositeForeignKeyJoinFindings = [.. partialCompositeForeignKeyJoinFindings.Where(f => f.Confidence <= minimumConfidence)];
        setOptionFindings = [.. setOptionFindings.Where(f => f.Confidence <= minimumConfidence)];
        catchAllPredicateFindings = [.. catchAllPredicateFindings.Where(f => f.Confidence <= minimumConfidence)];
        localVariablePredicateFindings = [.. localVariablePredicateFindings.Where(f => f.Confidence <= minimumConfidence)];
        filteredIndexParameterMismatchFindings = [.. filteredIndexParameterMismatchFindings.Where(f => f.Confidence <= minimumConfidence)];
        notInNullableSubqueryFindings = [.. notInNullableSubqueryFindings.Where(f => f.Confidence <= minimumConfidence)];
        nonUniqueUpdateSourceFindings = [.. nonUniqueUpdateSourceFindings.Where(f => f.Confidence <= minimumConfidence)];
        forcedSerialFindings = [.. forcedSerialFindings.Where(f => f.Confidence <= minimumConfidence)];
        untrustedConstraintFindings = [.. untrustedConstraintFindings.Where(f => f.Confidence <= minimumConfidence)];
        checkConstraintFindings = [.. checkConstraintFindings.Where(f => f.Confidence <= minimumConfidence)];
        cascadingForeignKeyFindings = [.. cascadingForeignKeyFindings.Where(f => f.Confidence <= minimumConfidence)];
        multiReferencedCteFindings = [.. multiReferencedCteFindings.Where(f => f.Confidence <= minimumConfidence)];
        nestedViewDepthFindings = [.. nestedViewDepthFindings.Where(f => f.Confidence <= minimumConfidence)];
        postExpansionJoinWidthFindings = [.. postExpansionJoinWidthFindings.Where(f => f.Confidence <= minimumConfidence)];
        selectStarViewFindings = [.. selectStarViewFindings.Where(f => f.Confidence <= minimumConfidence)];
        nonPersistedComputedColumnFindings = [.. nonPersistedComputedColumnFindings.Where(f => f.Confidence <= minimumConfidence)];
        selfReferencingDmlFindings = [.. selfReferencingDmlFindings.Where(f => f.Confidence <= minimumConfidence)];
        temporalTableHistoryIndexGapFindings = [.. temporalTableHistoryIndexGapFindings.Where(f => f.Confidence <= minimumConfidence)];
        moduleCompileFlagFindings = [.. moduleCompileFlagFindings.Where(f => f.Confidence <= minimumConfidence)];
        windowFrameFindings = [.. windowFrameFindings.Where(f => f.Confidence <= minimumConfidence)];
        waitForFindings = [.. waitForFindings.Where(f => f.Confidence <= minimumConfidence)];
        viewOrderingFindings = [.. viewOrderingFindings.Where(f => f.Confidence <= minimumConfidence)];
        transactionHygieneFindings = [.. transactionHygieneFindings.Where(f => f.Confidence <= minimumConfidence)];
        compositeIndexLeadingColumnFindings = [.. compositeIndexLeadingColumnFindings.Where(f => f.Confidence <= minimumConfidence)];
        indexHintFindings = [.. indexHintFindings.Where(f => f.Confidence <= minimumConfidence)];
        sessionDateSettingFindings = [.. sessionDateSettingFindings.Where(f => f.Confidence <= minimumConfidence)];
        cartesianJoinFindings = [.. cartesianJoinFindings.Where(f => f.Confidence <= minimumConfidence)];
        undersizedDeclarationFindings = [.. undersizedDeclarationFindings.Where(f => f.Confidence <= minimumConfidence)];
        truncateSwallowedFindings = [.. truncateSwallowedFindings.Where(f => f.Confidence <= minimumConfidence)];
        unindexedTempTableUsageFindings = [.. unindexedTempTableUsageFindings.Where(f => f.Confidence <= minimumConfidence)];
        outputParameterFindings = [.. outputParameterFindings.Where(f => f.Confidence <= minimumConfidence)];
        parameterReassignmentPredicateFindings = [.. parameterReassignmentPredicateFindings.Where(f => f.Confidence <= minimumConfidence)];
        codeMetricFindings = [.. codeMetricFindings.Where(f => f.Confidence <= minimumConfidence)];
        formattingFindings = [.. formattingFindings.Where(f => f.Confidence <= minimumConfidence)];
        namingFindings = [.. namingFindings.Where(f => f.Confidence <= minimumConfidence)];
        deadCodeFindings = [.. deadCodeFindings.Where(f => f.Confidence <= minimumConfidence)];
        duplicationFindings = [.. duplicationFindings.Where(f => f.Confidence <= minimumConfidence)];
        deprecatedSyntaxFindings = [.. deprecatedSyntaxFindings.Where(f => f.Confidence <= minimumConfidence)];
        statementShapeFindings = [.. statementShapeFindings.Where(f => f.Confidence <= minimumConfidence)];
        controlFlowRiskFindings = [.. controlFlowRiskFindings.Where(f => f.Confidence <= minimumConfidence)];
        securityFindings = [.. securityFindings.Where(f => f.Confidence <= minimumConfidence)];
        floatEqualityFindings = [.. floatEqualityFindings.Where(f => f.Confidence <= minimumConfidence)];
        queryAntiPatternFindings = [.. queryAntiPatternFindings.Where(f => f.Confidence <= minimumConfidence)];
        indexCoverageFindings = [.. indexCoverageFindings.Where(f => f.Confidence <= minimumConfidence)];
        triggerCorrectnessFindings = [.. triggerCorrectnessFindings.Where(f => f.Confidence <= minimumConfidence)];
        crossModuleLockOrderFindings = [.. crossModuleLockOrderFindings.Where(f => f.Confidence <= minimumConfidence)];
        triggerRecursionCycleFindings = [.. triggerRecursionCycleFindings.Where(f => f.Confidence <= minimumConfidence)];
        defaultNullableConstraintFindings = [.. defaultNullableConstraintFindings.Where(f => f.Confidence <= minimumConfidence)];
        tryCastComputedColumnPredicateFindings = [.. tryCastComputedColumnPredicateFindings.Where(f => f.Confidence <= minimumConfidence)];
        bareTopNoOrderByFindings = [.. bareTopNoOrderByFindings.Where(f => f.Confidence <= minimumConfidence)];
        stringConcatNullFindings = [.. stringConcatNullFindings.Where(f => f.Confidence <= minimumConfidence)];
        aggregateDivisionColumnstoreFindings = [.. aggregateDivisionColumnstoreFindings.Where(f => f.Confidence <= minimumConfidence)];

        return new ScanReport(
            new ParseHealthReport(fileHealth), tier1Findings, typedFindings, dynamicSqlFindings, expressionDerivedFindings, collationConflictFindings, writeLossFindings,
            tvfFenceFindings, scalarUdfFindings, columnCollationDriftFindings, crossTableTypeDriftFindings, procCallArgumentMismatchFindings, temporalBoundaryFindings,
            maxTypedColumnFindings, oversizedParameterFindings, underLengthParameterFindings, ansiPaddingMismatchFindings, partialCompositeForeignKeyJoinFindings, setOptionFindings,
            catchAllPredicateFindings, localVariablePredicateFindings, filteredIndexParameterMismatchFindings, notInNullableSubqueryFindings, nonUniqueUpdateSourceFindings, forcedSerialFindings,
            untrustedConstraintFindings, cascadingForeignKeyFindings, multiReferencedCteFindings,
            nestedViewDepthFindings, postExpansionJoinWidthFindings, selectStarViewFindings, unparameterizedDynamicSqlFindings,
            nonPersistedComputedColumnFindings,
            // TempTableExecShapeFindings needs a live database round trip (sys.dm_exec_describe_first_result_set)
            // this builder never issues - always empty here; LiveScanRunner merges the real result in afterward.
            [],
            selfReferencingDmlFindings,
            temporalTableHistoryIndexGapFindings,
            moduleCompileFlagFindings,
            windowFrameFindings, waitForFindings, viewOrderingFindings, transactionHygieneFindings,
            compositeIndexLeadingColumnFindings, indexHintFindings,
            sessionDateSettingFindings, cartesianJoinFindings, undersizedDeclarationFindings, truncateSwallowedFindings, unindexedTempTableUsageFindings,
            outputParameterFindings,
            // DatabaseConfigurationFindings needs a live database round trip against the
            // sys.databases and sys.database_query_store_options catalog views, which this
            // builder never issues, so it is always empty here. LiveScanRunner merges the
            // real result in afterward, the same pattern already established for
            // TempTableExecShapeFindings.
            [],
            parameterReassignmentPredicateFindings,
            codeMetricFindings,
            formattingFindings,
            namingFindings,
            deadCodeFindings,
            duplicationFindings,
            deprecatedSyntaxFindings,
            statementShapeFindings,
            controlFlowRiskFindings,
            securityFindings,
            // IndexDesignFindings needs CatalogIndex.IsClustered, populated only by a live catalog
            // read (LiveCatalogReader) - always empty here; LiveScanRunner merges the real result
            // in afterward, same pattern TempTableExecShapeFindings/DatabaseConfigurationFindings
            // already established.
            [],
            // IdentityRangeFindings needs CatalogColumn's identity seed/increment/current-value
            // fields, populated only by a live catalog read - always empty here; LiveScanRunner
            // merges the real result in afterward, same pattern as IndexDesignFindings above.
            [],
            floatEqualityFindings,
            queryAntiPatternFindings,
            indexCoverageFindings,
            triggerCorrectnessFindings,
            crossModuleLockOrderFindings,
            triggerRecursionCycleFindings,
            checkConstraintFindings,
            defaultNullableConstraintFindings,
            tryCastComputedColumnPredicateFindings,
            // StaleSelectStarViewFindings needs a live-only catalog registry (a view's own
            // compiled sys.columns row set, DatabaseCatalog.TryGetViewCompiledColumns) this
            // builder never populates - always empty here; LiveScanRunner merges the real result
            // in afterward, same pattern IndexDesignFindings/IdentityRangeFindings already
            // established.
            [],
            bareTopNoOrderByFindings,
            stringConcatNullFindings,
            aggregateDivisionColumnstoreFindings,
            securityPredicateIndexFindings,
            orderedSkippedConstructs, SkippedConstructSummary.From(orderedSkippedConstructs), typedPredicateSummary, dynamicSqlSummary);
    }

    private static (HashSet<string> UsableSourcePaths, int UsableCount) CollectUsableSourcePaths(
        IEnumerable<SqlParseResult> allParseResults, List<FileParseHealth> fileHealth)
    {
        var usableSourcePaths = new HashSet<string>(StringComparer.Ordinal);
        var usableCount = 0;

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
                usableSourcePaths.Add(result.SourcePath);
                usableCount++;
            }
        }

        return (usableSourcePaths, usableCount);
    }

    // See BuildFromParseResults' own call-site comment for why this runs to a fixed-point over
    // several rounds rather than once.
    private static List<DynamicSqlExtractionResult> ScanDynamicSqlWithOutputSummaries(
        IEnumerable<SqlParseResult> usableParseResults,
        ProcCallGraph procCallGraph,
        DatabaseCatalog catalog,
        ILiveRowValueFetcher? rowValueFetcher,
        int usableCount,
        IScanProgress progress)
    {
        const int maxOutputSummaryRounds = 5;
        var outputSummaryIndex = new Dictionary<(string, string), IReadOnlyList<string>>();
        List<DynamicSqlExtractionResult> dynamicSqlExtractions = [];
        using (var dynamicStage = progress.Begin("scanning dynamic SQL", usableCount * maxOutputSummaryRounds))
        {
            // Every round scans the exact same parsed modules - only outputSummaryIndex differs
            // between rounds. Materialized ONCE here rather than re-enumerating the lazy
            // usableParseResults query per round (which would reparse the whole corpus fresh up
            // to 5 times for no reason, since nothing about the parse itself changes round to
            // round) - a real, measured regression: on a database whose OUTPUT-summary chains
            // need several rounds to converge, this stage became the slowest in the scan purely
            // from redundant reparsing. Scoped to this `using` block and released by
            // PhaseMemory.ReleaseBetweenPhases() right after, exactly like every other phase's
            // own bounded materialization (CatalogBuilder's internal one, callgraph's, tier1's,
            // typed's) - never held simultaneously with another phase's.
            var forThisPhase = usableParseResults.ToList();

            var rounds = 0;
            for (var round = 0; round < maxOutputSummaryRounds; round++)
            {
                rounds = round + 1;

                // Each round is independent per parse result (the shared outputSummaryIndex is
                // read-only for the duration of a round and only folded in afterward), so the
                // round itself parallelizes even though the rounds are inherently sequential.
                // AsOrdered, unlike the Tier-1/typed passes below: those feed lists that get
                // sorted deterministically before reporting, but this one folds its results into
                // outputSummaryIndex, where two modules summarizing the SAME (proc, parameter)
                // key resolve last-writer-wins. Unordered completion would make which summary
                // survives depend on thread scheduling - a real determinism break, not a
                // cosmetic one.
                dynamicSqlExtractions = forThisPhase
                    .AsParallel()
                    .AsOrdered()
                    .Select(r =>
                    {
                        var scanned = DynamicSqlScannerV2.Scan(r, callGraph: procCallGraph, outputSummaryIndex: outputSummaryIndex, catalog: catalog, rowValueFetcher: rowValueFetcher);
                        dynamicStage.Advance();
                        return scanned;
                    })
                    .ToList();

                var discoveredCount = outputSummaryIndex.Count;
                foreach (var summary in dynamicSqlExtractions.SelectMany(r => r.OutputSummaries))
                {
                    outputSummaryIndex[(summary.QualifiedName, summary.ParameterName)] = summary.PossibleValues;
                }

                if (outputSummaryIndex.Count == discoveredCount)
                {
                    break;
                }
            }

            dynamicStage.Complete($"{rounds} round{(rounds == 1 ? "" : "s")} over {usableCount:N0} modules");
        }

        return dynamicSqlExtractions;
    }

    private static int VerdictRank(Verdict verdict) => verdict switch
    {
        Verdict.ScanForced => 0,
        Verdict.RangeSeek => 1,
        Verdict.Unknown => 2,
        _ => 3,
    };

    private static int InlineabilityRank(ScalarUdfInlineability inlineability) => inlineability switch
    {
        ScalarUdfInlineability.NotInlineable => 0,
        ScalarUdfInlineability.Unknown => 1,
        _ => 2,
    };
}
