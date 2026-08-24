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

        var parseResults = allParseResults as IReadOnlyList<SqlParseResult> ?? allParseResults.ToList();

        var (usableSourcePaths, usableCount) = CollectUsableSourcePaths(parseResults, fileHealth);

        IReadOnlyList<SqlParseResult> usableParseResults =
            parseResults.Where(r => usableSourcePaths.Contains(r.SourcePath)).ToList();

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

        var dynamicSqlExtractions = ScanDynamicSqlWithOutputSummaries(
            usableParseResults, procCallGraph, catalog, rowValueFetcher, usableCount, progress);
        PhaseMemory.ReleaseBetweenPhases();

        var dynamicSqlFindings = dynamicSqlExtractions.SelectMany(r => r.Findings).ToList();
        var dynamicSqlScripts = dynamicSqlExtractions.SelectMany(r => r.AnalyzableScripts).ToList();

        SelectIntoLineagePass.Apply(catalog, lineage, usableParseResults);

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

        IReadOnlyList<TriggerOrderFinding> triggerOrderFindings;
        using (var triggerOrderStage = progress.Begin("scanning trigger firing-order gaps"))
        {
            triggerOrderFindings = TriggerOrderScanner.Scan(catalog);
            triggerOrderStage.Complete($"{triggerOrderFindings.Count:N0} findings");
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

        IReadOnlyList<ColumnstoreUnsupportedColumnTypeFinding> columnstoreBatchModeDisqualifyingTypeFindings;
        using (var columnstoreBatchModeStage = progress.Begin("scanning columnstore batch-mode-disqualifying types"))
        {
            columnstoreBatchModeDisqualifyingTypeFindings = ColumnstoreUnsupportedColumnTypeScanner.Scan(catalog);
            columnstoreBatchModeStage.Complete($"{columnstoreBatchModeDisqualifyingTypeFindings.Count:N0} findings");
        }

        IReadOnlyList<MemoryOptimizedUnsupportedColumnTypeFinding> memoryOptimizedUnsupportedColumnTypeFindings;
        using (var memoryOptimizedColumnTypeStage = progress.Begin("scanning memory-optimized unsupported column types"))
        {
            memoryOptimizedUnsupportedColumnTypeFindings = MemoryOptimizedUnsupportedColumnTypeScanner.Scan(catalog);
            memoryOptimizedColumnTypeStage.Complete($"{memoryOptimizedUnsupportedColumnTypeFindings.Count:N0} findings");
        }

        IReadOnlyList<MemoryOptimizedUnsupportedIndexOptionFinding> memoryOptimizedUnsupportedIndexOptionFindings;
        using (var memoryOptimizedIndexOptionStage = progress.Begin("scanning memory-optimized unsupported index options"))
        {
            memoryOptimizedUnsupportedIndexOptionFindings = MemoryOptimizedUnsupportedIndexOptionScanner.Scan(catalog);
            memoryOptimizedIndexOptionStage.Complete($"{memoryOptimizedUnsupportedIndexOptionFindings.Count:N0} findings");
        }

        IReadOnlyList<MemoryOptimizedForeignKeyFinding> memoryOptimizedForeignKeyFindings;
        using (var memoryOptimizedForeignKeyStage = progress.Begin("scanning memory-optimized foreign keys"))
        {
            memoryOptimizedForeignKeyFindings = MemoryOptimizedForeignKeyScanner.Scan(catalog);
            memoryOptimizedForeignKeyStage.Complete($"{memoryOptimizedForeignKeyFindings.Count:N0} findings");
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

        List<WindowFunctionArgumentFinding> windowFunctionArgumentFindings;
        using (var windowFunctionArgumentStage = progress.Begin("scanning LAG/LEAD/PERCENTILE_CONT/PERCENTILE_DISC constant arguments", usableCount))
        {
            var unordered = usableParseResults
                .AsParallel()
                .SelectMany(r =>
                {
                    var findings = WindowFunctionArgumentScanner.Scan(r);
                    windowFunctionArgumentStage.Advance();
                    return findings;
                })
                .ToList();
            windowFunctionArgumentFindings = unordered
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

        List<MissingStatisticsFinding> missingStatisticsFindings;
        using (var missingStatisticsStage = progress.Begin("scanning missing-statistics/disabled-auto-create predicates", usableCount))
        {
            var unordered = usableParseResults
                .AsParallel()
                .SelectMany(r =>
                {
                    var findings = MissingStatisticsScanner.Scan(r, catalog);
                    missingStatisticsStage.Advance();
                    return findings;
                })
                .ToList();
            missingStatisticsFindings = unordered
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
                .OrderBy(f => f.QualifiedOrVariableName, StringComparer.Ordinal).ThenBy(f => f.SourcePath, StringComparer.Ordinal).ThenBy(f => f.Line).ThenBy(f => f.Column)
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
                .ThenBy(f => f.TempTableQualifiedName, StringComparer.Ordinal)
                .ThenBy(f => f.UsageLine).ThenBy(f => f.UsageColumn).ThenBy(f => f.Kind)
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
                .ThenBy(f => f.PredicateColumn)
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
                    var findings = DuplicationScanner.Scan(r, catalog);
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
                    var findings = DeprecatedSyntaxScanner.Scan(r, catalog);
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

        List<AlwaysEncryptedOrderByFinding> alwaysEncryptedOrderByFindings;
        using (var alwaysEncryptedOrderByStage = progress.Begin("scanning ORDER BY against Always Encrypted columns", usableCount))
        {
            var unordered = usableParseResults
                .AsParallel()
                .SelectMany(r =>
                {
                    var findings = AlwaysEncryptedOrderByScanner.Scan(r, catalog);
                    alwaysEncryptedOrderByStage.Advance();
                    return findings;
                })
                .ToList();
            alwaysEncryptedOrderByFindings = unordered
                .OrderBy(f => f.SourcePath, StringComparer.Ordinal).ThenBy(f => f.Line).ThenBy(f => f.Column)
                .ToList();
        }
        PhaseMemory.ReleaseBetweenPhases();

        List<OperandComparabilityFinding> operandComparabilityFindings;
        using (var operandComparabilityStage = progress.Begin("scanning operand comparability", usableCount))
        {
            var unordered = usableParseResults
                .AsParallel()
                .SelectMany(r =>
                {
                    var findings = OperandComparabilityScanner.Scan(r, catalog);
                    operandComparabilityStage.Advance();
                    return findings;
                })
                .ToList();
            operandComparabilityFindings = unordered
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

            crossModuleLockOrderFindings = CrossModuleLockOrderScanner.Scan(usableParseResults, catalog);
            lockOrderStage.Complete($"{crossModuleLockOrderFindings.Count:N0} findings");
        }
        PhaseMemory.ReleaseBetweenPhases();

        IReadOnlyList<TriggerRecursionCycleFinding> triggerRecursionCycleFindings;
        using (var triggerRecursionStage = progress.Begin("scanning multi-hop trigger recursion cycles"))
        {

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
                .OrderBy(f => f.SourcePath, StringComparer.Ordinal).ThenBy(f => f.Line).ThenBy(f => f.CteName, StringComparer.Ordinal)
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
                .ThenBy(f => f.Column)
                .ThenBy(f => f.ModuleQualifiedName, StringComparer.Ordinal)
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
                .ThenBy(f => f.ConsumerColumn)
                .ThenBy(f => f.ViewDepth)
                .ThenBy(f => f.ViewSourcePath, StringComparer.Ordinal)
                .ThenBy(f => f.ViewLine)
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

        var unparameterizedDynamicSqlFindings = dynamicSqlResult.UnparameterizedFindings
            .Where(f => f.Confidence <= minimumConfidence)
            .OrderBy(f => f.SourcePath, StringComparer.Ordinal)
            .ThenBy(f => f.Line)
            .ThenBy(f => f.Column)
            .ThenBy(f => f.Kind)
            .ToList();

        var typedPredicateSummary = TypedPredicateSummary.From(typedFindings);

        var dynamicSqlSummary = DynamicSqlSummary.From(dynamicSqlFindings);

        typedFindings = [.. typedFindings.Where(f => f.Verdict != Verdict.SeekPreserved && f.Confidence <= minimumConfidence)];
        tier1Findings = [.. tier1Findings.Where(f => f.Confidence <= minimumConfidence)];
        expressionDerivedFindings = [.. expressionDerivedFindings.Where(f => f.Confidence <= minimumConfidence)];
        collationConflictFindings = [.. collationConflictFindings.Where(f => f.Confidence <= minimumConfidence)];
        writeLossFindings = [.. writeLossFindings.Where(f => f.Confidence <= minimumConfidence)];
        tvfFenceFindings = [.. tvfFenceFindings.Where(f => f.Confidence <= minimumConfidence)];
        scalarUdfFindings = [.. scalarUdfFindings.Where(f => f.Confidence <= minimumConfidence)];

        tier1Findings = [.. tier1Findings
            .OrderBy(f => f.Indexed switch { true => 0, null => 1, false => 2 })
            .ThenBy(f => f.SourcePath, StringComparer.Ordinal)
            .ThenBy(f => f.Line)
            .ThenBy(f => f.Column)
            .ThenBy(f => f.Kind)];
        dynamicSqlFindings = [.. dynamicSqlFindings
            .OrderBy(f => f.SourcePath, StringComparer.Ordinal)
            .ThenBy(f => f.Line)
            .ThenBy(f => f.Column)
            .ThenBy(f => f.Outcome)];
        securityFindings = [.. securityFindings, .. SecurityScanner.FromDynamicSqlFindings(dynamicSqlFindings)];
        securityFindings = [.. securityFindings
            .OrderBy(f => f.Kind).ThenBy(f => f.SourcePath, StringComparer.Ordinal).ThenBy(f => f.Line).ThenBy(f => f.Column)];
        typedFindings = [.. typedFindings
            .OrderBy(f => VerdictRank(f.Verdict))
            .ThenBy(f => f.Column.Indexed switch { true => 0, null => 1, false => 2 })
            .ThenByDescending(f => f.Column.Depth)
            .ThenBy(f => f.SourcePath, StringComparer.Ordinal)
            .ThenBy(f => f.Line)
            .ThenBy(f => f.ColumnPosition)];
        expressionDerivedFindings = [.. expressionDerivedFindings.OrderBy(f => f.SourcePath, StringComparer.Ordinal).ThenBy(f => f.Line).ThenBy(f => f.ColumnPosition)];
        collationConflictFindings = [.. collationConflictFindings.OrderBy(f => f.SourcePath, StringComparer.Ordinal).ThenBy(f => f.Line).ThenBy(f => f.ColumnPosition)];
        writeLossFindings = [.. writeLossFindings.OrderBy(f => f.SourcePath, StringComparer.Ordinal).ThenBy(f => f.Line).ThenBy(f => f.ColumnPosition)];

        tvfFenceFindings = [.. tvfFenceFindings
            .OrderBy(f => f.Kind)
            .ThenByDescending(f => f.Depth)
            .ThenBy(f => f.SourcePath, StringComparer.Ordinal)
            .ThenBy(f => f.Line)
            .ThenBy(f => f.Column)];

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
        windowFunctionArgumentFindings = [.. windowFunctionArgumentFindings.Where(f => f.Confidence <= minimumConfidence)];
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
        alwaysEncryptedOrderByFindings = [.. alwaysEncryptedOrderByFindings.Where(f => f.Confidence <= minimumConfidence)];
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
        triggerOrderFindings = [.. triggerOrderFindings.Where(f => f.Confidence <= minimumConfidence)];
        missingStatisticsFindings = [.. missingStatisticsFindings.Where(f => f.Confidence <= minimumConfidence)];
        operandComparabilityFindings = [.. operandComparabilityFindings.Where(f => f.Confidence <= minimumConfidence)];

        return new ScanReport(
            new ParseHealthReport(fileHealth), tier1Findings, typedFindings, dynamicSqlFindings, expressionDerivedFindings, collationConflictFindings, writeLossFindings,
            tvfFenceFindings, scalarUdfFindings, columnCollationDriftFindings, crossTableTypeDriftFindings, procCallArgumentMismatchFindings, temporalBoundaryFindings,
            maxTypedColumnFindings, oversizedParameterFindings, underLengthParameterFindings, ansiPaddingMismatchFindings, partialCompositeForeignKeyJoinFindings, setOptionFindings,
            catchAllPredicateFindings, localVariablePredicateFindings, filteredIndexParameterMismatchFindings, notInNullableSubqueryFindings, nonUniqueUpdateSourceFindings, forcedSerialFindings,
            untrustedConstraintFindings, cascadingForeignKeyFindings, multiReferencedCteFindings,
            nestedViewDepthFindings, postExpansionJoinWidthFindings, selectStarViewFindings, unparameterizedDynamicSqlFindings,
            nonPersistedComputedColumnFindings,

            [],
            selfReferencingDmlFindings,
            temporalTableHistoryIndexGapFindings,
            moduleCompileFlagFindings,
            windowFrameFindings, waitForFindings, viewOrderingFindings, transactionHygieneFindings,
            compositeIndexLeadingColumnFindings, indexHintFindings,
            sessionDateSettingFindings, cartesianJoinFindings, undersizedDeclarationFindings, truncateSwallowedFindings, unindexedTempTableUsageFindings,
            outputParameterFindings,

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

            [],

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

            [],
            bareTopNoOrderByFindings,
            stringConcatNullFindings,
            aggregateDivisionColumnstoreFindings,
            securityPredicateIndexFindings,

            [],

            [],
            columnstoreBatchModeDisqualifyingTypeFindings,
            alwaysEncryptedOrderByFindings,
            triggerOrderFindings,
            missingStatisticsFindings,
            operandComparabilityFindings,
            memoryOptimizedUnsupportedColumnTypeFindings,
            memoryOptimizedUnsupportedIndexOptionFindings,
            memoryOptimizedForeignKeyFindings,
            windowFunctionArgumentFindings,
            orderedSkippedConstructs, SkippedConstructSummary.From(orderedSkippedConstructs), typedPredicateSummary, dynamicSqlSummary);
    }

    private static (HashSet<string> UsableSourcePaths, int UsableCount) CollectUsableSourcePaths(
        IEnumerable<SqlParseResult> allParseResults, List<FileParseHealth> fileHealth)
    {
        var usableSourcePaths = new HashSet<string>(StringComparer.Ordinal);
        var usableCount = 0;

        foreach (var result in allParseResults)
        {
            fileHealth.Add(ParseHealthReportBuilder.ToFileParseHealth(result));

            if (result.BatchCount > 0)
            {
                usableSourcePaths.Add(result.SourcePath);
                usableCount++;
            }
        }

        return (usableSourcePaths, usableCount);
    }

    private static List<DynamicSqlExtractionResult> ScanDynamicSqlWithOutputSummaries(
        IEnumerable<SqlParseResult> usableParseResults,
        ProcCallGraph procCallGraph,
        DatabaseCatalog catalog,
        ILiveRowValueFetcher? rowValueFetcher,
        int usableCount,
        IScanProgress progress)
    {
        const int maxOutputSummaryRounds = 5;

        var outputSummaryIndex = new Dictionary<(string, string), IReadOnlyList<string>>(TableColumnKeyComparer.Instance);
        List<DynamicSqlExtractionResult> dynamicSqlExtractions = [];
        using (var dynamicStage = progress.Begin("scanning dynamic SQL", usableCount * maxOutputSummaryRounds))
        {

            var forThisPhase = usableParseResults.ToList();

            var rounds = 0;
            for (var round = 0; round < maxOutputSummaryRounds; round++)
            {
                rounds = round + 1;

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
