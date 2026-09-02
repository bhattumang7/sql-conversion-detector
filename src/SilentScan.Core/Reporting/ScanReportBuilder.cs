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
            lineage = LineageResolver.Resolve(catalog, usableParseResults, lineageStage);
            lineageStage.Complete($"{lineage.AllRelations.Count:N0} relations");
        }

        IReadOnlyDictionary<string, Lineage.TvfFenceOrigin> tvfFenceMap;
        IReadOnlyDictionary<string, Lineage.ScalarUdfOrigin> scalarUdfMap;
        IReadOnlyDictionary<string, Lineage.ViewExpansionOrigin> viewExpansionMap;
        IReadOnlyDictionary<string, SelectStarViewCandidate> selectStarViewCandidates;
        List<Lineage.ViewDefinition> viewDefinitions;
        using (var fenceMapStage = progress.Begin("mapping TVF fences and scalar UDFs", usableCount))
        {
            (viewDefinitions, _) = Lineage.ViewDefinitionExtractor.Extract(usableParseResults, catalog.DefaultCollation, catalog.TypeAliases, ledger: null, fenceMapStage);
            tvfFenceMap = Lineage.TvfFenceMap.Build(viewDefinitions, catalog);
            scalarUdfMap = Lineage.ScalarUdfMap.Build(viewDefinitions, catalog);
            viewExpansionMap = Lineage.ViewExpansionMap.Build(viewDefinitions, catalog);
            selectStarViewCandidates = SelectStarViewScanner.BuildCandidates(viewDefinitions, viewExpansionMap, lineage, catalog.IdentifierComparer);
            fenceMapStage.Complete($"{tvfFenceMap.Count:N0} view/TVF layers inherit a fence, {scalarUdfMap.Count:N0} inherit a scalar UDF");
        }

        var callGraphLedger = new SkipLedger();
        ProcCallGraph procCallGraph;
        using (var callGraphStage = progress.Begin("building call graph", usableCount))
        {
            procCallGraph = ProcCallGraphBuilder.Build(usableParseResults, catalog, callGraphLedger, callGraphStage);
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

        var ruleContext = new RuleHarness.RuleContext(
            catalog, lineage, new SkipLedger(), procCallGraph,
            tvfFenceMap, scalarUdfMap, viewExpansionMap, viewDefinitions,
            selectStarViewCandidates, callerScopeByCalleeScope);
        var ruleResults = RuleHarness.RuleRunner.Run(RuleHarness.RuleRegistry.All, usableParseResults, ruleContext, minimumConfidence, progress);
        var ruleCrashes = ruleResults.Crashes;

        List<(List<SargabilityFinding> Findings, List<TemporalBoundaryPrecisionFinding> TemporalBoundaryFindings, List<JsonIndexRewriteFinding> JsonIndexRewriteFindings, IReadOnlyList<SkippedConstruct> Skipped)> tier1PerFile;
        using (var tier1Stage = progress.Begin("scanning syntactic predicates", usableCount))
        {
            tier1PerFile = usableParseResults
                .AsParallel()
                .Select(r =>
                {
                    tier1Stage.Advance(currentItem: r.SourcePath);
                    var fileLedger = new SkipLedger();
                    var (findings, temporalBoundaryFindings, jsonIndexRewriteFindings) = NonSargablePredicateScanner.ScanFull(r, catalog, lineage, ledger: fileLedger, callerScopeByCalleeScope: callerScopeByCalleeScope);
                    return (Findings: findings.ToList(), TemporalBoundaryFindings: temporalBoundaryFindings.ToList(), JsonIndexRewriteFindings: jsonIndexRewriteFindings.ToList(), Skipped: fileLedger.Entries);
                })
                .ToList();
        }

        var tier1Findings = tier1PerFile.SelectMany(p => p.Findings).ToList();
        var temporalBoundaryFindings = tier1PerFile.SelectMany(p => p.TemporalBoundaryFindings)
            .OrderBy(f => f.SourcePath, StringComparer.Ordinal).ThenBy(f => f.Line).ThenBy(f => f.Column).ToList();
        var jsonIndexRewriteFindings = tier1PerFile.SelectMany(p => p.JsonIndexRewriteFindings)
            .OrderBy(f => f.SourcePath, StringComparer.Ordinal).ThenBy(f => f.Line).ThenBy(f => f.Column).ToList();
        var tier1SkippedEntries = tier1PerFile.SelectMany(p => p.Skipped).ToList();
        PhaseMemory.ReleaseBetweenPhases();

        var tvfFenceFindings = ruleResults.For<TvfFenceFinding>("TvfFenceScanner").ToList();

        var scalarUdfFindings = ruleResults.For<ScalarUdfFinding>("ScalarUdfScanner").ToList();
        using (var schemaDependencyStage = progress.Begin("scanning schema-level scalar UDF dependencies", catalog.SchemaExpressions.Count))
        {
            var schemaDependencyFindings = SchemaDependencyScanner.Scan(catalog, schemaDependencyStage);
            scalarUdfFindings.AddRange(schemaDependencyFindings);
            schemaDependencyStage.Complete($"{schemaDependencyFindings.Count:N0} findings");
        }

        var securityFindings = ruleResults.For<SecurityFinding>("SecurityScanner").ToList();
        PhaseMemory.ReleaseBetweenPhases();

        List<PredicateExtractionResult> extractionResults;
        using (var typedStage = progress.Begin("scanning typed predicates", usableCount))
        {
            extractionResults = usableParseResults.AsParallel()
                .Select(r =>
                {
                    typedStage.Advance(currentItem: r.SourcePath);
                    return TypedPredicateExtractor.Extract(r, catalog, lineage, callerScopeByCalleeScope: callerScopeByCalleeScope);
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
        skippedConstructs.AddRange(ruleContext.Ledger.Entries);
        skippedConstructs.AddRange(ruleCrashes);

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

        temporalBoundaryFindings = [.. temporalBoundaryFindings.Where(f => f.Confidence <= minimumConfidence)];
        jsonIndexRewriteFindings = [.. jsonIndexRewriteFindings.Where(f => f.Confidence <= minimumConfidence)];
        oversizedParameterFindings = [.. oversizedParameterFindings.Where(f => f.Confidence <= minimumConfidence)];
        underLengthParameterFindings = [.. underLengthParameterFindings.Where(f => f.Confidence <= minimumConfidence)];
        ansiPaddingMismatchFindings = [.. ansiPaddingMismatchFindings.Where(f => f.Confidence <= minimumConfidence)];
        localVariablePredicateFindings = [.. localVariablePredicateFindings.Where(f => f.Confidence <= minimumConfidence)];
        filteredIndexParameterMismatchFindings = [.. filteredIndexParameterMismatchFindings.Where(f => f.Confidence <= minimumConfidence)];
        securityFindings = [.. securityFindings.Where(f => f.Confidence <= minimumConfidence)];

        var findingsByRuleId = new Dictionary<string, IReadOnlyList<IFinding>>(ruleResults.AllFindings, StringComparer.Ordinal)
        {
            ["NonSargablePredicateScanner"] = [.. tier1Findings, .. temporalBoundaryFindings, .. jsonIndexRewriteFindings],
            ["TypedPredicateExtractor"] = [
                .. typedFindings, .. expressionDerivedFindings, .. collationConflictFindings, .. writeLossFindings,
                .. oversizedParameterFindings, .. underLengthParameterFindings, .. ansiPaddingMismatchFindings,
                .. localVariablePredicateFindings, .. filteredIndexParameterMismatchFindings,
            ],
            ["DynamicSqlScanner"] = [.. dynamicSqlFindings, .. unparameterizedDynamicSqlFindings],
            ["TvfFenceScanner"] = tvfFenceFindings,
            ["ScalarUdfScanner"] = scalarUdfFindings,
            ["SecurityScanner"] = securityFindings,
        };

        return new ScanReport(
            new ParseHealthReport(fileHealth), findingsByRuleId,
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

        var outputSummaryIndex = new Dictionary<(string, string), IReadOnlyList<string>>(TableColumnKeyComparer.For(catalog));
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
                        dynamicStage.Advance(currentItem: r.SourcePath);
                        return DynamicSqlScannerV2.Scan(r, callGraph: procCallGraph, outputSummaryIndex: outputSummaryIndex, catalog: catalog, rowValueFetcher: rowValueFetcher);
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
