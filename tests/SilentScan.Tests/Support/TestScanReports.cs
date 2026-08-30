using SilentScan.Core.Catalog;
using SilentScan.Core.Diagnostics;
using SilentScan.Core.Lineage;
using SilentScan.Core.Parsing;
using SilentScan.Core.Predicates;
using SilentScan.Core.Reporting;
using SilentScan.Core.Rules;
using SilentScan.Core.TypeInference;

namespace SilentScan.Tests.Support;

public static class TestScanReports
{
    public static ScanReport Build(
        ParseHealthReport? ParseHealth = null,
        IReadOnlyList<SargabilityFinding>? Tier1Findings = null,
        IReadOnlyList<TypedPredicateFinding>? TypedFindings = null,
        IReadOnlyList<DynamicSqlFinding>? DynamicSqlFindings = null,
        IReadOnlyList<ExpressionDerivedFinding>? ExpressionDerivedFindings = null,
        IReadOnlyList<CollationConflictFinding>? CollationConflictFindings = null,
        IReadOnlyList<WriteLossFinding>? WriteLossFindings = null,
        IReadOnlyList<TvfFenceFinding>? TvfFenceFindings = null,
        IReadOnlyList<ScalarUdfFinding>? ScalarUdfFindings = null,
        IReadOnlyList<ColumnCollationDriftFinding>? ColumnCollationDriftFindings = null,
        IReadOnlyList<CrossTableTypeDriftFinding>? CrossTableTypeDriftFindings = null,
        IReadOnlyList<ProcCallArgumentMismatchFinding>? ProcCallArgumentMismatchFindings = null,
        IReadOnlyList<TemporalBoundaryPrecisionFinding>? TemporalBoundaryFindings = null,
        IReadOnlyList<MaxTypedColumnFinding>? MaxTypedColumnFindings = null,
        IReadOnlyList<OversizedParameterFinding>? OversizedParameterFindings = null,
        IReadOnlyList<UnderLengthParameterFinding>? UnderLengthParameterFindings = null,
        IReadOnlyList<AnsiPaddingMismatchFinding>? AnsiPaddingMismatchFindings = null,
        IReadOnlyList<PartialCompositeForeignKeyJoinFinding>? PartialCompositeForeignKeyJoinFindings = null,
        IReadOnlyList<SetOptionFinding>? SetOptionFindings = null,
        IReadOnlyList<CatchAllPredicateFinding>? CatchAllPredicateFindings = null,
        IReadOnlyList<LocalVariablePredicateFinding>? LocalVariablePredicateFindings = null,
        IReadOnlyList<FilteredIndexParameterMismatchFinding>? FilteredIndexParameterMismatchFindings = null,
        IReadOnlyList<NotInNullableSubqueryFinding>? NotInNullableSubqueryFindings = null,
        IReadOnlyList<NonUniqueUpdateSourceFinding>? NonUniqueUpdateSourceFindings = null,
        IReadOnlyList<ForcedSerialFinding>? ForcedSerialFindings = null,
        IReadOnlyList<UntrustedConstraintFinding>? UntrustedConstraintFindings = null,
        IReadOnlyList<CascadingForeignKeyFinding>? CascadingForeignKeyFindings = null,
        IReadOnlyList<MultiReferencedCteFinding>? MultiReferencedCteFindings = null,
        IReadOnlyList<NestedViewDepthFinding>? NestedViewDepthFindings = null,
        IReadOnlyList<PostExpansionJoinWidthFinding>? PostExpansionJoinWidthFindings = null,
        IReadOnlyList<SelectStarViewFinding>? SelectStarViewFindings = null,
        IReadOnlyList<UnparameterizedDynamicSqlFinding>? UnparameterizedDynamicSqlFindings = null,
        IReadOnlyList<NonPersistedComputedColumnFinding>? NonPersistedComputedColumnFindings = null,
        IReadOnlyList<TempTableExecShapeFinding>? TempTableExecShapeFindings = null,
        IReadOnlyList<SelfReferencingDmlFinding>? SelfReferencingDmlFindings = null,
        IReadOnlyList<TemporalTableHistoryIndexGapFinding>? TemporalTableHistoryIndexGapFindings = null,
        IReadOnlyList<ModuleCompileFlagFinding>? ModuleCompileFlagFindings = null,
        IReadOnlyList<WindowFrameFinding>? WindowFrameFindings = null,
        IReadOnlyList<WaitForFinding>? WaitForFindings = null,
        IReadOnlyList<ViewOrderingFinding>? ViewOrderingFindings = null,
        IReadOnlyList<TransactionHygieneFinding>? TransactionHygieneFindings = null,
        IReadOnlyList<CompositeIndexLeadingColumnFinding>? CompositeIndexLeadingColumnFindings = null,
        IReadOnlyList<IndexHintFinding>? IndexHintFindings = null,
        IReadOnlyList<SessionDateSettingFinding>? SessionDateSettingFindings = null,
        IReadOnlyList<CartesianJoinFinding>? CartesianJoinFindings = null,
        IReadOnlyList<TruncateSwallowedFinding>? TruncateSwallowedFindings = null,
        IReadOnlyList<UnindexedTempTableUsageFinding>? UnindexedTempTableUsageFindings = null,
        IReadOnlyList<OutputParameterFinding>? OutputParameterFindings = null,
        IReadOnlyList<DatabaseConfigurationFinding>? DatabaseConfigurationFindings = null,
        IReadOnlyList<ParameterReassignmentPredicateFinding>? ParameterReassignmentPredicateFindings = null,
        IReadOnlyList<CodeMetricFinding>? CodeMetricFindings = null,
        IReadOnlyList<FormattingFinding>? FormattingFindings = null,
        IReadOnlyList<NamingFinding>? NamingFindings = null,
        IReadOnlyList<DeadCodeFinding>? DeadCodeFindings = null,
        IReadOnlyList<DuplicationFinding>? DuplicationFindings = null,
        IReadOnlyList<DeprecatedSyntaxFinding>? DeprecatedSyntaxFindings = null,
        IReadOnlyList<StatementShapeFinding>? StatementShapeFindings = null,
        IReadOnlyList<ControlFlowRiskFinding>? ControlFlowRiskFindings = null,
        IReadOnlyList<SecurityFinding>? SecurityFindings = null,
        IReadOnlyList<IndexDesignFinding>? IndexDesignFindings = null,
        IReadOnlyList<IdentityRangeFinding>? IdentityRangeFindings = null,
        IReadOnlyList<FloatEqualityFinding>? FloatEqualityFindings = null,
        IReadOnlyList<QueryAntiPatternFinding>? QueryAntiPatternFindings = null,
        IReadOnlyList<IndexCoverageFinding>? IndexCoverageFindings = null,
        IReadOnlyList<TriggerCorrectnessFinding>? TriggerCorrectnessFindings = null,
        IReadOnlyList<CrossModuleLockOrderFinding>? CrossModuleLockOrderFindings = null,
        IReadOnlyList<TriggerRecursionCycleFinding>? TriggerRecursionCycleFindings = null,
        IReadOnlyList<CheckConstraintFinding>? CheckConstraintFindings = null,
        IReadOnlyList<DefaultNullableConstraintFinding>? DefaultNullableConstraintFindings = null,
        IReadOnlyList<TryCastComputedColumnPredicateFinding>? TryCastComputedColumnPredicateFindings = null,
        IReadOnlyList<StaleSelectStarViewFinding>? StaleSelectStarViewFindings = null,
        IReadOnlyList<BareTopNoOrderByFinding>? BareTopNoOrderByFindings = null,
        IReadOnlyList<StringConcatNullFinding>? StringConcatNullFindings = null,
        IReadOnlyList<AggregateDivisionColumnstoreFinding>? AggregateDivisionColumnstoreFindings = null,
        IReadOnlyList<SecurityPredicateIndexFinding>? SecurityPredicateIndexFindings = null,
        IReadOnlyList<DanglingObjectReferenceFinding>? DanglingObjectReferenceFindings = null,
        IReadOnlyList<ForcedParameterizationFinding>? ForcedParameterizationFindings = null,
        IReadOnlyList<ColumnstoreUnsupportedColumnTypeFinding>? ColumnstoreUnsupportedColumnTypeFindings = null,
        IReadOnlyList<AlwaysEncryptedOrderByFinding>? AlwaysEncryptedOrderByFindings = null,
        IReadOnlyList<TriggerOrderFinding>? TriggerOrderFindings = null,
        IReadOnlyList<MissingStatisticsFinding>? MissingStatisticsFindings = null,
        IReadOnlyList<OperandComparabilityFinding>? OperandComparabilityFindings = null,
        IReadOnlyList<MemoryOptimizedUnsupportedColumnTypeFinding>? MemoryOptimizedUnsupportedColumnTypeFindings = null,
        IReadOnlyList<MemoryOptimizedUnsupportedIndexOptionFinding>? MemoryOptimizedUnsupportedIndexOptionFindings = null,
        IReadOnlyList<MemoryOptimizedForeignKeyFinding>? MemoryOptimizedForeignKeyFindings = null,
        IReadOnlyList<WindowFunctionArgumentFinding>? WindowFunctionArgumentFindings = null,
        IReadOnlyList<SelectiveXmlIndexValueColumnFinding>? SelectiveXmlIndexValueColumnFindings = null,
        IReadOnlyList<FloatOrderDependentAggregateFinding>? FloatOrderDependentAggregateFindings = null,
        IReadOnlyList<AlwaysEncryptedKeyColumnFinding>? AlwaysEncryptedKeyColumnFindings = null,
        IReadOnlyList<AlterColumnSafetyFinding>? AlterColumnSafetyFindings = null,
        IReadOnlyList<SpExecuteSqlParameterMismatchFinding>? SpExecuteSqlParameterMismatchFindings = null,
        IReadOnlyList<SkippedConstruct>? SkippedConstructs = null,
        SkippedConstructSummary? SkippedConstructSummary = null,
        TypedPredicateSummary? TypedPredicateSummary = null,
        DynamicSqlSummary? DynamicSqlSummary = null,
        int SchemaVersion = ScanReport.CurrentSchemaVersion)
    {
        var findingsByRuleId = new Dictionary<string, IReadOnlyList<IFinding>>(StringComparer.Ordinal);
        void Set<TFinding>(string ruleId, IReadOnlyList<TFinding>? findings)
            where TFinding : IFinding
        {
            if (findings is { Count: > 0 })
            {
                findingsByRuleId[ruleId] = [.. findingsByRuleId.TryGetValue(ruleId, out var existing) ? existing : [], .. findings.Cast<IFinding>()];
            }
        }

        Set("NonSargablePredicateScanner", Tier1Findings);
        Set("NonSargablePredicateScanner", TemporalBoundaryFindings);
        Set("TypedPredicateExtractor", TypedFindings);
        Set("TypedPredicateExtractor", ExpressionDerivedFindings);
        Set("TypedPredicateExtractor", CollationConflictFindings);
        Set("TypedPredicateExtractor", WriteLossFindings);
        Set("TypedPredicateExtractor", OversizedParameterFindings);
        Set("TypedPredicateExtractor", UnderLengthParameterFindings);
        Set("TypedPredicateExtractor", AnsiPaddingMismatchFindings);
        Set("TypedPredicateExtractor", LocalVariablePredicateFindings);
        Set("TypedPredicateExtractor", FilteredIndexParameterMismatchFindings);
        Set("DynamicSqlScanner", DynamicSqlFindings);
        Set("DynamicSqlScanner", UnparameterizedDynamicSqlFindings);
        Set("TvfFenceScanner", TvfFenceFindings);
        Set("ScalarUdfScanner", ScalarUdfFindings);
        Set("SecurityScanner", SecurityFindings);
        Set("ColumnCollationDriftScanner", ColumnCollationDriftFindings);
        Set("CrossTableTypeDriftScanner", CrossTableTypeDriftFindings);
        Set("ProcCallArgumentMismatchScanner", ProcCallArgumentMismatchFindings);
        Set("MaxTypedColumnScanner", MaxTypedColumnFindings);
        Set("PartialCompositeForeignKeyJoinScanner", PartialCompositeForeignKeyJoinFindings);
        Set("SetOptionScanner", SetOptionFindings);
        Set("CatchAllPredicateScanner", CatchAllPredicateFindings);
        Set("NotInNullableSubqueryScanner", NotInNullableSubqueryFindings);
        Set("NonUniqueUpdateSourceScanner", NonUniqueUpdateSourceFindings);
        Set("ForcedSerialScanner", ForcedSerialFindings);
        Set("UntrustedConstraintScanner", UntrustedConstraintFindings);
        Set("CascadingForeignKeyScanner", CascadingForeignKeyFindings);
        Set("MultiReferencedCteScanner", MultiReferencedCteFindings);
        Set("NestedViewDepthScanner", NestedViewDepthFindings);
        Set("PostExpansionJoinWidthScanner", PostExpansionJoinWidthFindings);
        Set("SelectStarViewScanner", SelectStarViewFindings);
        Set("NonPersistedComputedColumnScanner", NonPersistedComputedColumnFindings);
        Set("TempTableExecShapeScanner", TempTableExecShapeFindings);
        Set("SelfReferencingDmlScanner", SelfReferencingDmlFindings);
        Set("TemporalTableHistoryIndexGapScanner", TemporalTableHistoryIndexGapFindings);
        Set("ModuleCompileFlagScanner", ModuleCompileFlagFindings);
        Set("WindowFrameScanner", WindowFrameFindings);
        Set("WaitForScanner", WaitForFindings);
        Set("ViewOrderingScanner", ViewOrderingFindings);
        Set("TransactionHygieneScanner", TransactionHygieneFindings);
        Set("CompositeIndexLeadingColumnScanner", CompositeIndexLeadingColumnFindings);
        Set("IndexHintScanner", IndexHintFindings);
        Set("SessionDateSettingScanner", SessionDateSettingFindings);
        Set("CartesianJoinScanner", CartesianJoinFindings);
        Set("TruncateSwallowedScanner", TruncateSwallowedFindings);
        Set("UnindexedTempTableUsageScanner", UnindexedTempTableUsageFindings);
        Set("OutputParameterScanner", OutputParameterFindings);
        Set("DatabaseConfigurationScanner", DatabaseConfigurationFindings);
        Set("ParameterReassignmentPredicateScanner", ParameterReassignmentPredicateFindings);
        Set("CodeMetricScanner", CodeMetricFindings);
        Set("FormattingScanner", FormattingFindings);
        Set("NamingScanner", NamingFindings);
        Set("DeadCodeScanner", DeadCodeFindings);
        Set("DuplicationScanner", DuplicationFindings);
        Set("DeprecatedSyntaxScanner", DeprecatedSyntaxFindings);
        Set("StatementShapeScanner", StatementShapeFindings);
        Set("ControlFlowRiskScanner", ControlFlowRiskFindings);
        Set("IndexDesignScanner", IndexDesignFindings);
        Set("IdentityRangeScanner", IdentityRangeFindings);
        Set("FloatEqualityPredicateScanner", FloatEqualityFindings);
        Set("QueryAntiPatternScanner", QueryAntiPatternFindings);
        Set("IndexCoverageScanner", IndexCoverageFindings);
        Set("TriggerCorrectnessScanner", TriggerCorrectnessFindings);
        Set("CrossModuleLockOrderScanner", CrossModuleLockOrderFindings);
        Set("TriggerRecursionCycleScanner", TriggerRecursionCycleFindings);
        Set("CheckConstraintScanner", CheckConstraintFindings);
        Set("DefaultNullableConstraintScanner", DefaultNullableConstraintFindings);
        Set("TryCastComputedColumnPredicateScanner", TryCastComputedColumnPredicateFindings);
        Set("StaleSelectStarViewScanner", StaleSelectStarViewFindings);
        Set("BareTopNoOrderByScanner", BareTopNoOrderByFindings);
        Set("StringConcatNullScanner", StringConcatNullFindings);
        Set("AggregateDivisionColumnstoreScanner", AggregateDivisionColumnstoreFindings);
        Set("SecurityPredicateIndexScanner", SecurityPredicateIndexFindings);
        Set("DanglingObjectReferenceScanner", DanglingObjectReferenceFindings);
        Set("ForcedParameterizationScanner", ForcedParameterizationFindings);
        Set("ColumnstoreUnsupportedColumnTypeScanner", ColumnstoreUnsupportedColumnTypeFindings);
        Set("AlwaysEncryptedOrderByScanner", AlwaysEncryptedOrderByFindings);
        Set("TriggerOrderScanner", TriggerOrderFindings);
        Set("MissingStatisticsScanner", MissingStatisticsFindings);
        Set("OperandComparabilityScanner", OperandComparabilityFindings);
        Set("MemoryOptimizedUnsupportedColumnTypeScanner", MemoryOptimizedUnsupportedColumnTypeFindings);
        Set("MemoryOptimizedUnsupportedIndexOptionScanner", MemoryOptimizedUnsupportedIndexOptionFindings);
        Set("MemoryOptimizedForeignKeyScanner", MemoryOptimizedForeignKeyFindings);
        Set("WindowFunctionArgumentScanner", WindowFunctionArgumentFindings);
        Set("SelectiveXmlIndexValueColumnScanner", SelectiveXmlIndexValueColumnFindings);
        Set("FloatOrderDependentAggregateScanner", FloatOrderDependentAggregateFindings);
        Set("AlwaysEncryptedKeyColumnScanner", AlwaysEncryptedKeyColumnFindings);
        Set("AlterColumnSafetyScanner", AlterColumnSafetyFindings);
        Set("SpExecuteSqlParameterMismatchScanner", SpExecuteSqlParameterMismatchFindings);

        return new ScanReport(
            ParseHealth ?? new ParseHealthReport([]),
            findingsByRuleId,
            SkippedConstructs ?? [],
            SkippedConstructSummary ?? SkippedConstructSummary.From([]),
            TypedPredicateSummary ?? TypedPredicateSummary.From([]),
            DynamicSqlSummary ?? DynamicSqlSummary.From([]),
            SchemaVersion);
    }
}
