using System.Globalization;
using SilentScan.Core.Catalog;
using SilentScan.Core.Diagnostics;
using SilentScan.Core.Lineage;
using SilentScan.Core.Parsing;
using SilentScan.Core.Predicates;
using SilentScan.Core.Rules;
using SilentScan.Core.Reporting.Sarif;
using SilentScan.Core.TypeInference;

namespace SilentScan.Core.Reporting.Readable;

public static class ReadableScanReportWriter
{
    private const string WhereHeader = "Where";

    private const string ColumnHeader = "Column";

    private const string ModuleHeader = "Module";

    private const string IndexedHeader = "Indexed";

    private const string DetailHeader = "Detail";

    private const string ConstraintHeader = "Constraint";

    private const string OperatorHeader = "Operator";

    private const string ParameterHeader = "Parameter";

    private const string TableHeader = "Table";

    private const string IndexHeader = "Index";

    private const string UnknownDisplay = "unknown";

    private const string DanglingObjectReferenceRuleId = "DanglingObjectReferenceScanner";

    private const string DatabaseConfigurationRuleId = "DatabaseConfigurationScanner";

    private const string DynamicSqlRuleId = "DynamicSqlScanner";

    private const string TempTableExecShapeRuleId = "TempTableExecShapeScanner";

    private const string ExecResultSetsShapeRuleId = "ExecResultSetsShapeScanner";

    public static string Write(ScanReport report, string title, ReadableStyle style, string? pathBase = null, ReadableVerbosity verbosity = ReadableVerbosity.Brief) =>
        ReadableDocumentRenderer.Render(BuildDocument(report, title, pathBase, verbosity), style);

    public static ReadableDocument BuildDocument(ScanReport report, string title, string? pathBase = null, ReadableVerbosity verbosity = ReadableVerbosity.Brief)
    {
        List<ReadableBlock> blocks = [new ReadableBlock.Heading(1, title)];
        blocks.AddRange(BuildSections(report, 2, pathBase, verbosity));
        return new ReadableDocument(blocks);
    }

    public static IReadOnlyList<ReadableBlock> BuildSections(ScanReport report, int headingLevel, string? pathBase = null, ReadableVerbosity verbosity = ReadableVerbosity.Brief)
    {
        ArgumentNullException.ThrowIfNull(report);

        var blocks = new List<ReadableBlock>();
        blocks.AddRange(Summary(report, headingLevel));
        blocks.AddRange(CollationConflicts(report, headingLevel, pathBase));
        blocks.AddRange(TypedSection(
            report, Verdict.ScanForced, headingLevel, pathBase,
            "Implicit conversions that force a scan",
            "The column side of these comparisons is converted, not the value side, so no index on the column can be seeked - the engine reads every row and converts it before it can compare. These are the findings this tool exists to find; the ones on an indexed column, inherited through a view layer, are listed first."));
        blocks.AddRange(TypedSection(
            report, Verdict.RangeSeek, headingLevel, pathBase,
            "Implicit conversions that degrade the seek",
            "The column still converts, but under a Windows collation the engine can bound the search with GetRangeThroughConvert - cheaper than the scan above, dearer than the seek the column would have had with matching types."));
        blocks.AddRange(ExpressionDerived(report, headingLevel, pathBase));
        blocks.AddRange(WriteLoss(report, headingLevel, pathBase));
        blocks.AddRange(Tier1(report, headingLevel, pathBase));
        blocks.AddRange(TvfFence(report, headingLevel, pathBase));
        blocks.AddRange(ScalarUdf(report, headingLevel, pathBase));
        blocks.AddRange(ColumnCollationDrift(report, headingLevel, pathBase));
        blocks.AddRange(AnsiPaddingOffColumn(report, headingLevel, pathBase));
        blocks.AddRange(CrossTableTypeDrift(report, headingLevel, pathBase));
        blocks.AddRange(TriggerOrder(report, headingLevel, pathBase));
        blocks.AddRange(ProcCallArgumentMismatch(report, headingLevel, pathBase));
        blocks.AddRange(ProcCallTableValuedArgumentMismatch(report, headingLevel, pathBase));
        blocks.AddRange(SpExecuteSqlParameterMismatch(report, headingLevel, pathBase));
        blocks.AddRange(TemporalBoundary(report, headingLevel, pathBase));
        blocks.AddRange(MaxTypedColumn(report, headingLevel, pathBase));
        blocks.AddRange(ColumnstoreUnsupportedColumnType(report, headingLevel, pathBase));
        blocks.AddRange(SelectiveXmlIndexValueColumn(report, headingLevel, pathBase));
        blocks.AddRange(MemoryOptimizedUnsupportedColumnType(report, headingLevel, pathBase));
        blocks.AddRange(MemoryOptimizedUnsupportedIndexOption(report, headingLevel, pathBase));
        blocks.AddRange(MemoryOptimizedForeignKey(report, headingLevel, pathBase));
        blocks.AddRange(MemoryOptimizedSchemaOnlyDurability(report, headingLevel, pathBase));
        blocks.AddRange(NonPersistedComputedColumn(report, headingLevel, pathBase));
        blocks.AddRange(OversizedParameter(report, headingLevel, pathBase));
        blocks.AddRange(UnderLengthParameter(report, headingLevel, pathBase));
        blocks.AddRange(AnsiPaddingMismatch(report, headingLevel, pathBase));
        blocks.AddRange(CatchAllPredicate(report, headingLevel, pathBase));
        blocks.AddRange(LocalVariablePredicate(report, headingLevel, pathBase));
        blocks.AddRange(FilteredIndexParameterMismatch(report, headingLevel, pathBase));
        blocks.AddRange(NotInNullableSubquery(report, headingLevel, pathBase));
        blocks.AddRange(NonUniqueUpdateSource(report, headingLevel, pathBase));
        blocks.AddRange(CheckConstraintPredicateContradiction(report, headingLevel, pathBase));
        blocks.AddRange(ForcedSerial(report, headingLevel, pathBase));
        blocks.AddRange(UntrustedConstraint(report, headingLevel, pathBase));
        blocks.AddRange(CascadingForeignKey(report, headingLevel, pathBase));
        blocks.AddRange(MultiReferencedCte(report, headingLevel, pathBase));
        blocks.AddRange(RecursiveCteAnchorTypeMismatch(report, headingLevel, pathBase));
        blocks.AddRange(NestedViewDepth(report, headingLevel, pathBase));
        blocks.AddRange(PostExpansionJoinWidth(report, headingLevel, pathBase));
        blocks.AddRange(SelectStarView(report, headingLevel, pathBase));
        blocks.AddRange(PartialCompositeForeignKeyJoin(report, headingLevel, pathBase));
        blocks.AddRange(OuterJoinPredicateCollapse(report, headingLevel, pathBase));
        blocks.AddRange(SetOption(report, headingLevel, pathBase));
        blocks.AddRange(UnparameterizedDynamicSql(report, headingLevel, pathBase));
        blocks.AddRange(TempTableExecShape(report, headingLevel, pathBase));
        blocks.AddRange(ExecResultSetsShape(report, headingLevel, pathBase));
        blocks.AddRange(SelfReferencingDml(report, headingLevel, pathBase));
        blocks.AddRange(TemporalTableHistoryIndexGap(report, headingLevel, pathBase));
        blocks.AddRange(GeneratedAlwaysColumnAssignment(report, headingLevel, pathBase));
        blocks.AddRange(ModuleCompileFlag(report, headingLevel, pathBase));
        blocks.AddRange(WindowFrame(report, headingLevel, pathBase));
        blocks.AddRange(WindowFunctionArgument(report, headingLevel, pathBase));
        blocks.AddRange(StringSplitArgument(report, headingLevel, pathBase));
        blocks.AddRange(BoundedStringBuiltinTruncation(report, headingLevel, pathBase));
        blocks.AddRange(WaitFor(report, headingLevel, pathBase));
        blocks.AddRange(CursorCloseOnCommit(report, headingLevel, pathBase));
        blocks.AddRange(ViewOrdering(report, headingLevel, pathBase));
        blocks.AddRange(TransactionHygiene(report, headingLevel, pathBase));
        blocks.AddRange(CompositeIndexLeadingColumn(report, headingLevel, pathBase));
        blocks.AddRange(MissingStatistics(report, headingLevel, pathBase));
        blocks.AddRange(IndexHint(report, headingLevel, pathBase));
        blocks.AddRange(SessionDateSetting(report, headingLevel, pathBase));
        blocks.AddRange(CartesianJoin(report, headingLevel, pathBase));
        blocks.AddRange(TruncateSwallowed(report, headingLevel, pathBase));
        blocks.AddRange(UnindexedTempTableUsage(report, headingLevel, pathBase));
        blocks.AddRange(OutputParameter(report, headingLevel, pathBase));
        blocks.AddRange(DatabaseConfiguration(report, headingLevel));
        blocks.AddRange(ParameterReassignmentPredicate(report, headingLevel, pathBase));
        blocks.AddRange(CodeMetric(report, headingLevel, pathBase));
        blocks.AddRange(Formatting(report, headingLevel, pathBase));
        blocks.AddRange(Naming(report, headingLevel, pathBase));
        blocks.AddRange(DeadCode(report, headingLevel, pathBase));
        blocks.AddRange(Duplication(report, headingLevel, pathBase));
        blocks.AddRange(DeprecatedSyntax(report, headingLevel, pathBase));
        blocks.AddRange(StatementShape(report, headingLevel, pathBase));
        blocks.AddRange(ControlFlowRisk(report, headingLevel, pathBase));
        blocks.AddRange(Security(report, headingLevel, pathBase));
        blocks.AddRange(IndexDesign(report, headingLevel, pathBase));
        blocks.AddRange(ForcedParameterization(report, headingLevel, pathBase));
        blocks.AddRange(IdentityRange(report, headingLevel, pathBase));
        blocks.AddRange(FloatEquality(report, headingLevel, pathBase));
        blocks.AddRange(FloatOrderDependentAggregate(report, headingLevel, pathBase));
        blocks.AddRange(DynamicDataMasking(report, headingLevel, pathBase));
        blocks.AddRange(AlwaysEncryptedOrderBy(report, headingLevel, pathBase));
        blocks.AddRange(AlwaysEncryptedKeyColumn(report, headingLevel, pathBase));
        blocks.AddRange(AlterColumnSafety(report, headingLevel, pathBase));
        blocks.AddRange(DropProtectedObject(report, headingLevel, pathBase));
        blocks.AddRange(OnlineRebuildLegacyLob(report, headingLevel, pathBase));
        blocks.AddRange(OperandComparability(report, headingLevel, pathBase));
        blocks.AddRange(QueryAntiPattern(report, headingLevel, pathBase));
        blocks.AddRange(IndexCoverage(report, headingLevel, pathBase));
        blocks.AddRange(TriggerCorrectness(report, headingLevel, pathBase));
        blocks.AddRange(CrossModuleLockOrder(report, headingLevel, pathBase));
        blocks.AddRange(TriggerRecursionCycle(report, headingLevel, pathBase));
        blocks.AddRange(CheckConstraint(report, headingLevel, pathBase));
        blocks.AddRange(DefaultNullableConstraint(report, headingLevel, pathBase));
        blocks.AddRange(TryCastComputedColumnPredicate(report, headingLevel, pathBase));
        blocks.AddRange(StaleSelectStarView(report, headingLevel, pathBase));
        blocks.AddRange(BareTopNoOrderBy(report, headingLevel, pathBase));
        blocks.AddRange(StringConcatNull(report, headingLevel, pathBase));
        blocks.AddRange(AggregateDivisionColumnstore(report, headingLevel, pathBase));
        blocks.AddRange(SecurityPredicateIndex(report, headingLevel, pathBase));
        blocks.AddRange(DanglingObjectReference(report, headingLevel, pathBase));
        blocks.AddRange(TypedSection(
            report, Verdict.Unknown, headingLevel, pathBase,
            "Comparisons that could not be classified",
            "Something the verdict rules need was missing or ambiguous - most often a collation that no DDL in the scan pinned down. These are neither clean nor flagged; they are unanswered, and they are listed rather than dropped so the counts above cannot be read as covering them.",
            verbosity));
        blocks.AddRange(TypedSection(
            report, Verdict.OperandClash, headingLevel, pathBase,
            "Comparisons between genuinely incompatible types",
            "The oracle-probed type matrix confirms this exact type pair does not compile as a comparison at all (e.g. TIME vs a date-family type, or a GUID vs a string) - distinct from an unclassified comparison above: this one has a definitive answer, and the answer is that the comparison itself cannot run as written."));
        blocks.AddRange(DynamicSql(report, headingLevel, pathBase, verbosity));
        blocks.AddRange(ParseFailures(report, headingLevel, pathBase, verbosity));
        blocks.AddRange(UnanalyzedObjects(report, headingLevel, pathBase, verbosity));
        blocks.AddRange(SkippedConstructs(report, headingLevel));

        return blocks;
    }

    private static ReadableBlock.Paragraph BriefPointer(int count, string noun) =>
        new($"{Count(count, noun)} - not listed individually here; re-run with --verbosity full to see each one.");

    private static IEnumerable<ReadableBlock> Summary(ScanReport report, int level)
    {
        var health = report.ParseHealth;
        var summary = report.TypedPredicateSummary;

        yield return new ReadableBlock.Heading(level, "Summary");

        var parsed = health.TotalFiles - health.FilesWithErrors;
        yield return new ReadableBlock.Paragraph(
            $"{Count(health.TotalFiles, "file")} scanned, {parsed} parsed cleanly ({Percent(health.ParseSuccessRate)}).");

        var counts = new List<IReadOnlyList<string>>();
        AddCount(counts, "Collation conflicts (query does not compile)", report.Find<CollationConflictFinding>(nameof(TypedPredicateExtractor)).Count);
        AddCount(counts, "Implicit conversions forcing a scan", summary.ScanForcedCount, summary.DistinctScanForcedCount);
        AddCount(counts, "Implicit conversions degrading the seek", summary.RangeSeekCount, summary.DistinctRangeSeekCount);
        AddCount(counts, "Expression-derived columns in predicates", report.Find<ExpressionDerivedFinding>(nameof(TypedPredicateExtractor)).Count);
        AddCount(counts, "Assignments risking silent data loss", report.Find<WriteLossFinding>(nameof(TypedPredicateExtractor)).Count);
        AddCount(counts, "Non-sargable predicate patterns", report.Find<SargabilityFinding>(nameof(NonSargablePredicateScanner)).Count);
        AddCount(counts, "Multi-statement/CLR TVF references acting as optimization fences", report.Find<TvfFenceFinding>(nameof(TvfFenceScanner)).Count);
        AddCount(counts, "Scalar UDF calls (per-row cost, non-sargable when predicate-context)", report.Find<ScalarUdfFinding>(nameof(ScalarUdfScanner)).Count);
        AddCount(counts, "Columns whose collation drifts from the database/tempdb default", report.Find<ColumnCollationDriftFinding>(nameof(ColumnCollationDriftScanner)).Count);
        AddCount(counts, "Columns with ANSI_PADDING OFF in their own catalog state", report.Find<AnsiPaddingOffColumnFinding>(nameof(AnsiPaddingOffColumnScanner)).Count);
        AddCount(counts, "Foreign-key column pairs whose types/collations drift", report.Find<CrossTableTypeDriftFinding>(nameof(CrossTableTypeDriftScanner)).Count);
        AddCount(counts, "Tables with undefined AFTER trigger firing order", report.Find<TriggerOrderFinding>(nameof(TriggerOrderScanner)).Count);
        AddCount(counts, "EXEC call-site arguments risking silent data loss at the parameter boundary", report.Find<ProcCallArgumentMismatchFinding>(nameof(ProcCallArgumentMismatchScanner)).Count);
        AddCount(counts, "EXEC call-site table-valued parameter columns risking silent data loss when their caller-side table variable was populated", report.Find<ProcCallTableValuedArgumentMismatchFinding>(nameof(ProcCallTableValuedArgumentMismatchScanner)).Count);
        AddCount(counts, "sp_executesql call-site arguments risking silent data loss against their own declared parameter type", report.Find<SpExecuteSqlParameterMismatchFinding>(nameof(SpExecuteSqlParameterMismatchScanner)).Count);
        AddCount(counts, "BETWEEN predicates silently excluding rows at an imprecise end-of-period boundary", report.Find<TemporalBoundaryPrecisionFinding>(nameof(NonSargablePredicateScanner)).Count);
        AddCount(counts, "MAX-typed columns (can never be an index key)", report.Find<MaxTypedColumnFinding>(nameof(MaxTypedColumnScanner)).Count(f => f.Kind == NonIndexableColumnFindingKind.MaxLength));
        AddCount(counts, "Legacy large-object columns (can never appear in any index)", report.Find<MaxTypedColumnFinding>(nameof(MaxTypedColumnScanner)).Count(f => f.Kind == NonIndexableColumnFindingKind.LegacyLargeObject));
        AddCount(counts, "Columnstore-unsupported-type columns participating in a columnstore index (does not deploy)", report.Find<ColumnstoreUnsupportedColumnTypeFinding>(nameof(ColumnstoreUnsupportedColumnTypeScanner)).Count);
        AddCount(counts, "Secondary selective XML indexes over an oversized/large-object value column (does not deploy)", report.Find<SelectiveXmlIndexValueColumnFinding>(nameof(SelectiveXmlIndexValueColumnScanner)).Count);
        AddCount(counts, "Unsupported column type on a memory-optimized table (does not deploy)", report.Find<MemoryOptimizedUnsupportedColumnTypeFinding>(nameof(MemoryOptimizedUnsupportedColumnTypeScanner)).Count);
        AddCount(counts, "Unsupported index option on a memory-optimized table (does not deploy)", report.Find<MemoryOptimizedUnsupportedIndexOptionFinding>(nameof(MemoryOptimizedUnsupportedIndexOptionScanner)).Count);
        AddCount(counts, "Unsupported memory-optimized foreign key (does not deploy)", report.Find<MemoryOptimizedForeignKeyFinding>(nameof(MemoryOptimizedForeignKeyScanner)).Count);
        AddCount(counts, "Memory-optimized table declared SCHEMA_ONLY durability (data lost on restart)", report.Find<MemoryOptimizedSchemaOnlyDurabilityFinding>(nameof(MemoryOptimizedSchemaOnlyDurabilityScanner)).Count);
        AddCount(counts, "Non-persisted computed columns", report.Find<NonPersistedComputedColumnFinding>(nameof(NonPersistedComputedColumnScanner)).Count);
        AddCount(counts, "Predicates comparing a column against an oversized parameter/variable", report.Find<OversizedParameterFinding>(nameof(TypedPredicateExtractor)).Count);
        AddCount(counts, "Predicates comparing a column against an under-length parameter/variable", report.Find<UnderLengthParameterFinding>(nameof(TypedPredicateExtractor)).Count);
        AddCount(counts, "LIKE predicates that can never match a non-ANSI-padded column", report.Find<AnsiPaddingMismatchFinding>(nameof(TypedPredicateExtractor)).Count);
        AddCount(counts, "Catch-all / kitchen-sink optional-filter predicates", report.Find<CatchAllPredicateFinding>(nameof(CatchAllPredicateScanner)).Count);
        AddCount(counts, "Predicates against a local variable (cardinality-estimate risk only)", report.Find<LocalVariablePredicateFinding>(nameof(TypedPredicateExtractor)).Count);
        AddCount(counts, "Filtered index matched only against a literal, query uses a parameter/variable", report.Find<FilteredIndexParameterMismatchFinding>(nameof(TypedPredicateExtractor)).Count);
        AddCount(counts, "Predicates against a reassigned formal parameter (sniffing defeated)", report.Find<ParameterReassignmentPredicateFinding>(nameof(ParameterReassignmentPredicateScanner)).Count);
        AddCount(counts, "Size/complexity metric thresholds exceeded", report.Find<CodeMetricFinding>(nameof(CodeMetricScanner)).Count);
        AddCount(counts, "Formatting and layout risks", report.Find<FormattingFinding>(nameof(FormattingScanner)).Count);
        AddCount(counts, "Naming and identifier risks", report.Find<NamingFinding>(nameof(NamingScanner)).Count);
        AddCount(counts, "Dead code and control-flow risks", report.Find<DeadCodeFinding>(nameof(DeadCodeScanner)).Count);
        AddCount(counts, "Duplicated/redundant code shapes", report.Find<DuplicationFinding>(nameof(DuplicationScanner)).Count);
        AddCount(counts, "Task comments and deprecated syntax", report.Find<DeprecatedSyntaxFinding>(nameof(DeprecatedSyntaxScanner)).Count);
        AddCount(counts, "Statement-shape risks", report.Find<StatementShapeFinding>(nameof(StatementShapeScanner)).Count);
        AddCount(counts, "Cursor and control-flow risks", report.Find<ControlFlowRiskFinding>(nameof(ControlFlowRiskScanner)).Count);
        AddCount(counts, "Security", report.Find<SecurityFinding>(nameof(SecurityScanner)).Count);
        AddCount(counts, "Physical/schema index design (heap/clustered-key quality)", report.Find<IndexDesignFinding>(nameof(IndexDesignScanner)).Count);
        AddCount(counts, "Forced-parameterization-defeating query shapes", report.Find<ForcedParameterizationFinding>(nameof(ForcedParameterizationScanner)).Count);
        AddCount(counts, "Identity/sequence range signals", report.Find<IdentityRangeFinding>(nameof(IdentityRangeScanner)).Count);
        AddCount(counts, "Float/real equality predicates", report.Find<FloatEqualityFinding>(nameof(FloatEqualityPredicateScanner)).Count);
        AddCount(counts, "Float/real columns in order-dependent aggregates", report.Find<FloatOrderDependentAggregateFinding>(nameof(FloatOrderDependentAggregateScanner)).Count);
        AddCount(counts, "Dynamic Data Masking silently defeated", report.Find<DynamicDataMaskingFinding>(nameof(DynamicDataMaskingScanner)).Count);
        AddCount(counts, "Always Encrypted ORDER BY", report.Find<AlwaysEncryptedOrderByFinding>(nameof(AlwaysEncryptedOrderByScanner)).Count);
        AddCount(counts, "Always Encrypted non-enclave key column", report.Find<AlwaysEncryptedKeyColumnFinding>(nameof(AlwaysEncryptedKeyColumnScanner)).Count);
        AddCount(counts, "ALTER COLUMN safety", report.Find<AlterColumnSafetyFinding>(nameof(AlterColumnSafetyScanner)).Count);
        AddCount(counts, "DROP against a protected object", report.Find<DropProtectedObjectFinding>(nameof(DropProtectedObjectScanner)).Count);
        AddCount(counts, "Online index rebuild blocked by a legacy large-object column", report.Find<OnlineRebuildLegacyLobFinding>(nameof(OnlineRebuildLegacyLobScanner)).Count);
        AddCount(counts, "Operand not comparable (xml/json/legacy large object/spatial)", report.Find<OperandComparabilityFinding>(nameof(OperandComparabilityScanner)).Count);
        AddCount(counts, "Query anti-patterns", report.Find<QueryAntiPatternFinding>(nameof(QueryAntiPatternScanner)).Count);
        AddCount(counts, "Index-coverage shapes", report.Find<IndexCoverageFinding>(nameof(IndexCoverageScanner)).Count);
        AddCount(counts, "Trigger correctness", report.Find<TriggerCorrectnessFinding>(nameof(TriggerCorrectnessScanner)).Count);
        AddCount(counts, "Cross-module lock ordering", report.Find<CrossModuleLockOrderFinding>(nameof(CrossModuleLockOrderScanner)).Count);
        AddCount(counts, "Multi-hop trigger recursion cycles", report.Find<TriggerRecursionCycleFinding>(nameof(TriggerRecursionCycleScanner)).Count);
        AddCount(counts, "CHECK constraint text correctness (NULL handling, IDENTITY-column placement)", report.Find<CheckConstraintFinding>(nameof(CheckConstraintScanner)).Count);
        AddCount(counts, "DEFAULT constraint on a still-nullable column", report.Find<DefaultNullableConstraintFinding>(nameof(DefaultNullableConstraintScanner)).Count);
        AddCount(counts, "TRY_CAST computed column referenced in a predicate", report.Find<TryCastComputedColumnPredicateFinding>(nameof(TryCastComputedColumnPredicateScanner)).Count);
        AddCount(counts, "SELECT * view stale against base table's current shape", report.Find<StaleSelectStarViewFinding>(nameof(StaleSelectStarViewScanner)).Count);
        AddCount(counts, "Bare TOP with no ORDER BY", report.Find<BareTopNoOrderByFinding>(nameof(BareTopNoOrderByScanner)).Count);
        AddCount(counts, "+ concatenation of a nullable string column with no NULL guard", report.Find<StringConcatNullFinding>(nameof(StringConcatNullScanner)).Count);
        AddCount(counts, "CASE-guarded aggregate division on a columnstore-backed table", report.Find<AggregateDivisionColumnstoreFinding>(nameof(AggregateDivisionColumnstoreScanner)).Count);
        AddCount(counts, "RLS predicate with no supporting index", report.Find<SecurityPredicateIndexFinding>(nameof(SecurityPredicateIndexScanner)).Count);
        AddCount(counts, "Reference to a nonexistent object", report.Find<DanglingObjectReferenceFinding>(DanglingObjectReferenceRuleId).Count);
        AddCount(counts, "NOT IN predicates over a nullable subquery column (correctness trap)", report.Find<NotInNullableSubqueryFinding>(nameof(NotInNullableSubqueryScanner)).Count);
        AddCount(counts, "UPDATE...FROM joins whose source carries no uniqueness guarantee", report.Find<NonUniqueUpdateSourceFinding>(nameof(NonUniqueUpdateSourceScanner)).Count);
        AddCount(counts, "Predicates provably contradicting a trusted CHECK constraint or NOT NULL fact", report.Find<CheckConstraintPredicateContradictionFinding>(nameof(CheckConstraintPredicateContradictionScanner)).Count);
        AddCount(counts, "Constructs that force a statement/query plan serial", report.Find<ForcedSerialFinding>(nameof(ForcedSerialScanner)).Count);
        AddCount(counts, "Untrusted FK/CHECK constraints", report.Find<UntrustedConstraintFinding>(nameof(UntrustedConstraintScanner)).Count);
        AddCount(counts, "Foreign keys with a cascading ON DELETE/UPDATE action", report.Find<CascadingForeignKeyFinding>(nameof(CascadingForeignKeyScanner)).Count);
        AddCount(counts, "CTEs referenced 2+ times downstream of their own WITH clause", report.Find<MultiReferencedCteFinding>(nameof(MultiReferencedCteScanner)).Count);
        AddCount(counts, "Recursive CTE anchor/recursive member column type disagreements", report.Find<RecursiveCteAnchorTypeMismatchFinding>(nameof(RecursiveCteAnchorTypeMismatchScanner)).Count);
        AddCount(counts, "Views/inline TVFs nested 2+ view/TVF layers deep", report.Find<NestedViewDepthFinding>(nameof(NestedViewDepthScanner)).Count);
        AddCount(counts, "Queries whose expanded join width exceeds their written FROM/JOIN count", report.Find<PostExpansionJoinWidthFinding>(nameof(PostExpansionJoinWidthScanner)).Count);
        AddCount(counts, "Consumers narrowing a nested SELECT * view's frozen column list", report.Find<SelectStarViewFinding>(nameof(SelectStarViewScanner)).Count);
        AddCount(counts, "JOINs matching some but not all of a composite foreign key's columns", report.Find<PartialCompositeForeignKeyJoinFinding>(nameof(PartialCompositeForeignKeyJoinScanner)).Count);
        AddCount(counts, "OUTER JOIN predicates that silently collapse to an INNER JOIN", report.Find<OuterJoinPredicateCollapseFinding>(nameof(OuterJoinPredicateCollapseScanner)).Count);
        AddCount(counts, "SET options silently disabling a filtered index/indexed view the module touches", report.Find<SetOptionFinding>(nameof(SetOptionScanner)).Count);
        AddCount(counts, "Dynamic SQL call sites concatenating a proven-constant value instead of parameterizing it", report.Find<UnparameterizedDynamicSqlFinding>(DynamicSqlRuleId).Count);
        AddCount(counts, "INSERT INTO #temp EXEC proc shape mismatches", report.Find<TempTableExecShapeFinding>(TempTableExecShapeRuleId).Count);
        AddCount(counts, "EXEC ... WITH RESULT SETS shape mismatches", report.Find<ExecResultSetsShapeFinding>(ExecResultSetsShapeRuleId).Count);
        AddCount(counts, "Self-referencing DML (Halloween Protection risk)", report.Find<SelfReferencingDmlFinding>(nameof(SelfReferencingDmlScanner)).Count);
        AddCount(counts, "Temporal table history-side index gaps", report.Find<TemporalTableHistoryIndexGapFinding>(nameof(TemporalTableHistoryIndexGapScanner)).Count);
        AddCount(counts, "Explicit assignments to a GENERATED ALWAYS temporal period column", report.Find<GeneratedAlwaysColumnAssignmentFinding>(nameof(GeneratedAlwaysColumnAssignmentScanner)).Count);
        AddCount(counts, "Module compile flags (WITH RECOMPILE / TVF database-collation return)", report.Find<ModuleCompileFlagFinding>(nameof(ModuleCompileFlagScanner)).Count);
        AddCount(counts, "RANGE window-function frames", report.Find<WindowFrameFinding>(nameof(WindowFrameScanner)).Count);
        AddCount(counts, "LAG/LEAD/PERCENTILE_CONT/PERCENTILE_DISC/TABLESAMPLE out-of-range constant arguments", report.Find<WindowFunctionArgumentFinding>(nameof(WindowFunctionArgumentScanner)).Count);
        AddCount(counts, "STRING_SPLIT separator arguments not exactly one character", report.Find<StringSplitArgumentFinding>(nameof(StringSplitArgumentScanner)).Count);
        AddCount(counts, "REPLICATE/REPLACE/SPACE constant-provable result truncation", report.Find<BoundedStringBuiltinTruncationFinding>(nameof(BoundedStringBuiltinTruncationScanner)).Count);
        AddCount(counts, "WAITFOR DELAY/TIME", report.Find<WaitForFinding>(nameof(WaitForScanner)).Count);
        AddCount(counts, "Cursors silently closed by CURSOR_CLOSE_ON_COMMIT then fetched", report.Find<CursorCloseOnCommitFinding>(nameof(CursorCloseOnCommitScanner)).Count);
        AddCount(counts, "View/inline TVF ordering not guaranteed", report.Find<ViewOrderingFinding>(nameof(ViewOrderingScanner)).Count);
        AddCount(counts, "Unresolved BEGIN TRANSACTION", report.Find<TransactionHygieneFinding>(nameof(TransactionHygieneScanner)).Count);
        AddCount(counts, "Composite index leading-column violations", report.Find<CompositeIndexLeadingColumnFinding>(nameof(CompositeIndexLeadingColumnScanner)).Count);
        AddCount(counts, "Predicate columns with no applicable statistic and auto-create disabled", report.Find<MissingStatisticsFinding>(nameof(MissingStatisticsScanner)).Count);
        AddCount(counts, "INDEX hints naming a nonexistent or non-seekable index", report.Find<IndexHintFinding>(nameof(IndexHintScanner)).Count);
        AddCount(counts, "SET DATEFORMAT/DATEFIRST mid-module", report.Find<SessionDateSettingFinding>(nameof(SessionDateSettingScanner)).Count);
        AddCount(counts, "Cartesian and always-false joins", report.Find<CartesianJoinFinding>(nameof(CartesianJoinScanner)).Count);
        AddCount(counts, "TRUNCATE swallowed by an empty/non-rethrowing CATCH", report.Find<TruncateSwallowedFinding>(nameof(TruncateSwallowedScanner)).Count);
        AddCount(counts, "Unindexed SELECT INTO temp table usage", report.Find<UnindexedTempTableUsageFinding>(nameof(UnindexedTempTableUsageScanner)).Count);
        AddCount(counts, "Unassigned OUTPUT parameters", report.Find<OutputParameterFinding>(nameof(OutputParameterScanner)).Count);
        AddCount(counts, "Database-level configuration flags", report.Find<DatabaseConfigurationFinding>(DatabaseConfigurationRuleId).Count);
        AddCount(counts, "Comparisons that could not be classified", summary.UnknownCount);
        AddCount(counts, "Comparisons between genuinely incompatible types", summary.OperandClashCount);
        AddCount(counts, "Dynamic SQL call sites not statically analyzable", report.DynamicSqlSummary.UnanalyzableCount + report.DynamicSqlSummary.InnerParseFailedCount);
        AddCount(counts, "Dynamic SQL call sites partially analyzed (a fragment was elided)", report.DynamicSqlSummary.PartiallyAnalyzedCount);
        AddCount(counts, "Files that failed to parse", health.FilesWithErrors);
        AddCount(counts, "Constructs skipped as out of scope", report.SkippedConstructSummary.TotalCount);

        if (counts.Count == 0)
        {
            yield return new ReadableBlock.Paragraph("No findings.");
        }
        else
        {
            yield return new ReadableBlock.Table(["What", "Occurrences", "Distinct"], counts);
        }

        yield return new ReadableBlock.Paragraph(
            $"Base rate: {Count(summary.TotalClassified, "column comparison")} classified " +
            $"({summary.DistinctTotalClassified} distinct), of which {summary.SeekPreservedCount} keep their seek. " +
            "Seek-preserving comparisons are counted but not listed - there is nothing to act on.");
    }

    private static void AddCount(List<IReadOnlyList<string>> rows, string label, int occurrences, int? distinct = null)
    {
        if (occurrences == 0)
        {
            return;
        }

        rows.Add([
            label,
            occurrences.ToString(CultureInfo.InvariantCulture),
            distinct is { } d ? d.ToString(CultureInfo.InvariantCulture) : "-",
        ]);
    }

    private static IEnumerable<ReadableBlock> TypedSection(
        ScanReport report, Verdict verdict, int level, string? pathBase, string title, string explanation,
        ReadableVerbosity verbosity = ReadableVerbosity.Full)
    {
        var findings = report.Find<TypedPredicateFinding>(nameof(TypedPredicateExtractor)).Where(f => f.Verdict == verdict).ToList();
        if (findings.Count == 0)
        {
            yield break;
        }

        yield return new ReadableBlock.Heading(level, $"{title} ({findings.Count})");
        yield return new ReadableBlock.Paragraph(explanation);

        if (verbosity == ReadableVerbosity.Brief)
        {
            yield return BriefPointer(findings.Count, "comparison");
            yield break;
        }

        yield return new ReadableBlock.Table(
            [WhereHeader, ColumnHeader, "Column type", "Compared with", IndexedHeader, "Introduced by"],
            [.. findings.Select(f => TypedRow(f, pathBase))]);
    }

    private static IReadOnlyList<string> TypedRow(TypedPredicateFinding finding, string? pathBase) =>
    [
        Where(finding.SourcePath, finding.Line, finding.DynamicSqlCallSite, pathBase, finding.Confidence),
        $"{finding.Column.TableQualifiedName}.{finding.Column.ColumnName}",
        DescribeType(finding.Column.Type),
        $"{finding.Operator} {DescribeOperand(finding.OtherOperand)}{(finding.UnknownReason is { } reason ? $" ({reason})" : string.Empty)}",
        DescribeIndexed(finding.Column),
        DescribeOrigin(finding.Column, pathBase),
    ];

    private static string DescribeIndexed(PredicateOperand.Column column) => column.Indexed switch
    {
        true => column.IndexName is { } indexName ? $"yes ({indexName})" : "yes",
        false => "no",
        null => "unresolved",
    };

    private static IEnumerable<ReadableBlock> CollationConflicts(ScanReport report, int level, string? pathBase)
    {
        if (report.Find<CollationConflictFinding>(nameof(TypedPredicateExtractor)).Count == 0)
        {
            yield break;
        }

        yield return new ReadableBlock.Heading(level, $"Collation conflicts ({report.Find<CollationConflictFinding>(nameof(TypedPredicateExtractor)).Count})");
        yield return new ReadableBlock.Paragraph(
            "These comparisons put two explicitly different collations on either side, which SQL Server rejects at compile time (Msg 468) - the query does not run at all. That outranks any seek-versus-scan question, so they are listed first.");
        yield return new ReadableBlock.Paragraph(RuleDocSite.Url(SarifRuleCatalog.CollationConflictRuleId));
        yield return new ReadableBlock.Table(
            [WhereHeader, "Left", "Right", OperatorHeader],
            [.. report.Find<CollationConflictFinding>(nameof(TypedPredicateExtractor)).Select(f => new List<string>
            {
                Where(f.SourcePath, f.Line, f.DynamicSqlCallSite, pathBase, f.Confidence),
                $"{f.FirstTableQualifiedName}.{f.FirstColumnName} COLLATE {f.FirstCollationName}",
                $"{f.SecondTableQualifiedName}.{f.SecondColumnName} COLLATE {f.SecondCollationName}",
                f.Operator,
            })]);
    }

    private static IEnumerable<ReadableBlock> ExpressionDerived(ScanReport report, int level, string? pathBase)
    {
        if (report.Find<ExpressionDerivedFinding>(nameof(TypedPredicateExtractor)).Count == 0)
        {
            yield break;
        }

        yield return new ReadableBlock.Heading(level, $"Expression-derived columns in predicates ({report.Find<ExpressionDerivedFinding>(nameof(TypedPredicateExtractor)).Count})");
        yield return new ReadableBlock.Paragraph(
            "By the time these columns reach the predicate they are the result of an expression a view or function computed, not a stored column. An index on whatever feeds them cannot be seeked through that expression. " +
            "The ones that DO have a real index sitting underneath the expression - the cases actually worth rewriting the predicate for - are listed first.");
        yield return new ReadableBlock.Paragraph(RuleDocSite.Url(SarifRuleCatalog.ExpressionDerivedRuleId));
        yield return new ReadableBlock.Table(
            [WhereHeader, ColumnHeader, "Computed by", "Underlying base columns"],
            [.. report.Find<ExpressionDerivedFinding>(nameof(TypedPredicateExtractor))
                .OrderByDescending(f => f.UnderlyingBaseColumns.Any(bc => bc.Indexed))
                .ThenBy(f => f.SourcePath, StringComparer.Ordinal)
                .ThenBy(f => f.Line)
                .Select(f => new List<string>
            {
                Where(f.SourcePath, f.Line, f.DynamicSqlCallSite, pathBase, f.Confidence),
                f.ColumnName,
                f.TransformationChain.Count == 0
                    ? UnknownDisplay
                    : string.Join(" <- ", f.TransformationChain.Select(site => DescribeTransformationSite(site, pathBase))),
                f.UnderlyingBaseColumns.Count == 0
                    ? "none traceable"
                    : string.Join(", ", f.UnderlyingBaseColumns.Select(bc => $"{bc.TableQualifiedName}.{bc.ColumnName}{(bc.Indexed ? " (indexed)" : string.Empty)}")),
            })]);
    }

    private static IEnumerable<ReadableBlock> WriteLoss(ScanReport report, int level, string? pathBase)
    {
        if (report.Find<WriteLossFinding>(nameof(TypedPredicateExtractor)).Count == 0)
        {
            yield break;
        }

        yield return new ReadableBlock.Heading(level, $"Assignments risking silent data loss ({report.Find<WriteLossFinding>(nameof(TypedPredicateExtractor)).Count})");
        yield return new ReadableBlock.Paragraph(
            "Each of these writes a value whose static type carries more information than its target can hold - T-SQL rounds, truncates, or replaces the value with no error raised, so nothing here shows up as a failed statement. A case T-SQL itself refuses to run (a too-long string, an overflowing integer) is not listed - those already fail loudly on their own.");
        foreach (var group in report.Find<WriteLossFinding>(nameof(TypedPredicateExtractor)).GroupBy(f => f.Kind).OrderBy(g => g.Key))
        {
            var ordered = group.ToList();
            yield return new ReadableBlock.Heading(level + 1, $"{HumanizeKindName(group.Key.ToString())} ({ordered.Count})");
            yield return new ReadableBlock.Paragraph(RuleDocSite.Url(SarifRuleCatalog.WriteLossRuleId(group.Key)));
            yield return new ReadableBlock.Table(
                [WhereHeader, ColumnHeader, "Target type", "Source type", "Risk"],
                [.. ordered.Select(f => new List<string>
                {
                    Where(f.SourcePath, f.Line, f.DynamicSqlCallSite, pathBase, f.Confidence),
                    f.TableQualifiedName is { } table ? $"{table}.{f.ColumnName}" : f.ColumnName,
                    f.TargetType.ToString(),
                    f.SourceType.ToString(),
                    DescribeWriteLossKind(f.Kind),
                })]);
        }
    }

    private static string DescribeWriteLossKind(WriteLossKind kind) => kind switch
    {
        WriteLossKind.UnicodeToNonUnicodeReplacement => "Unicode characters outside the target's codepage become '?'",
        WriteLossKind.ApproximateToExactTruncation => "fractional part silently dropped",
        WriteLossKind.NumericScaleNarrowing => "digits past the target's scale silently rounded away",
        WriteLossKind.TemporalPrecisionLoss => "time-of-day silently dropped",
        WriteLossKind.LengthTruncation => "characters/bytes past the target's length silently dropped",
        WriteLossKind.TemporalScaleNarrowing => "fractional-second digits silently rounded away",
        WriteLossKind.TemporalOffsetDropped => "UTC offset silently dropped",
        _ => kind.ToString(),
    };

    private static IEnumerable<ReadableBlock> Tier1(ScanReport report, int level, string? pathBase)
    {
        if (report.Find<SargabilityFinding>(nameof(NonSargablePredicateScanner)).Count == 0)
        {
            yield break;
        }

        yield return new ReadableBlock.Heading(level, $"Non-sargable predicate patterns ({report.Find<SargabilityFinding>(nameof(NonSargablePredicateScanner)).Count})");
        yield return new ReadableBlock.Paragraph(
            "These are visible in the SQL text alone, with no type information needed: the column is not left bare on its side of the comparison, so an index on it cannot be seeked. Ones on a column confirmed to be indexed come first within each pattern.");

        foreach (var group in report.Find<SargabilityFinding>(nameof(NonSargablePredicateScanner))
            .GroupBy(f => f.Kind)
            .OrderBy(g => g.Key))
        {
            var ordered = group
                .OrderByDescending(f => f.Indexed == true)
                .ThenBy(f => f.SourcePath, StringComparer.Ordinal)
                .ThenBy(f => f.Line)
                .ToList();

            yield return new ReadableBlock.Heading(level + 1, $"{Tier1Title(group.Key)} ({ordered.Count})");
            yield return new ReadableBlock.Paragraph(Tier1Explanation(group.Key));
            yield return new ReadableBlock.Paragraph(RuleDocSite.Url(SarifRuleCatalog.Tier1RuleId(group.Key)));
            yield return new ReadableBlock.Table(
                [WhereHeader, ColumnHeader, IndexedHeader, DetailHeader],
                [.. ordered.Select(f => new List<string>
                {
                    Where(f.SourcePath, f.Line, f.DynamicSqlCallSite, pathBase, f.Confidence),
                    f.TableQualifiedName is { } table ? $"{table}.{f.ColumnName}" : f.ColumnName,
                    f.Indexed switch { true => "yes", false => "no", null => "unresolved" },
                    f.Detail ?? "-",
                })]);
        }
    }

    private static string Tier1Title(SargabilityFindingKind kind) => kind switch
    {
        SargabilityFindingKind.FunctionWrappedColumn => "Column wrapped in a function",
        SargabilityFindingKind.CastOrConvertOnColumn => "CAST/CONVERT applied to the column",
        SargabilityFindingKind.ColumnArithmetic => "Arithmetic on the column",
        SargabilityFindingKind.LeadingWildcardLike => "LIKE with a leading wildcard",
        SargabilityFindingKind.CaseFoldOnColumn => "UPPER/LOWER applied to the column",
        SargabilityFindingKind.DateFunctionOnColumn => "Date-part function applied to the column",
        SargabilityFindingKind.CharindexOrLeftOnColumn => "CHARINDEX/LEFT applied to the column",
        SargabilityFindingKind.LikePatternNotLiteral => "LIKE with a non-literal pattern",
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "Unhandled SargabilityFindingKind."),
    };

    private static string Tier1Explanation(SargabilityFindingKind kind) => kind switch
    {
        SargabilityFindingKind.FunctionWrappedColumn =>
            "The index stores the column's values, not the function's results, so the engine must compute the function for every row before it can compare.",
        SargabilityFindingKind.CastOrConvertOnColumn =>
            "Same as any other function on the column - the converted value is not what the index is ordered by. Converting the other side instead usually keeps the seek.",
        SargabilityFindingKind.ColumnArithmetic =>
            "The column is part of an expression rather than standing alone. Moving the arithmetic to the other side of the comparison usually restores the seek.",
        SargabilityFindingKind.LeadingWildcardLike =>
            "A pattern starting with % has no known prefix, and a b-tree can only seek on a prefix - the whole index or table is read.",
        SargabilityFindingKind.CaseFoldOnColumn =>
            "Oracle-verified: the wrap forces a scan regardless of the column's collation family - it is never a no-op for the PLAN, even when it's a no-op for the RESULT SET. See the finding's own Detail for whether this specific column's wrap is safe to delete or needs a real rewrite.",
        SargabilityFindingKind.DateFunctionOnColumn =>
            "Oracle-verified: the date-part function forces a per-row scan just like any other function wrap. Usually rewritable to a sargable literal date range (e.g. YEAR(col)=2024 becomes col >= '2024-01-01' AND col < '2025-01-01') that restores the seek.",
        SargabilityFindingKind.CharindexOrLeftOnColumn =>
            "See the finding's own Detail for whether this specific comparison is a prefix match with a real sargable rewrite (col LIKE 'x%'), or a genuine substring search with none.",
        _ =>
            "The pattern is a variable or expression, so the plan cannot be built around a known prefix; the engine has to assume the worst at compile time.",
    };

    private static IEnumerable<ReadableBlock> TvfFence(ScanReport report, int level, string? pathBase)
    {
        if (report.Find<TvfFenceFinding>(nameof(TvfFenceScanner)).Count == 0)
        {
            yield break;
        }

        yield return new ReadableBlock.Heading(level, $"Multi-statement/CLR TVF references acting as optimization fences ({report.Find<TvfFenceFinding>(nameof(TvfFenceScanner)).Count})");
        yield return new ReadableBlock.Paragraph(
            "A multi-statement or CLR table-valued function's body is opaque to the optimizer: its result is materialized into a statistics-less worktable and the reference carries a fixed cardinality guess (1 row under the legacy CE, 100 under 2014+), which propagates into the surrounding plan's join order, join types and memory grant. The call site reads identically to a harmless inline TVF - only the catalog tells them apart. Correlated APPLY references and fences inherited invisibly through a view/TVF layer are listed first: no engine-version mitigation rescues either.");

        foreach (var group in report.Find<TvfFenceFinding>(nameof(TvfFenceScanner))
            .GroupBy(f => f.Kind)
            .OrderBy(g => g.Key))
        {
            var ordered = group
                .OrderByDescending(f => f.Depth)
                .ThenBy(f => f.SourcePath, StringComparer.Ordinal)
                .ThenBy(f => f.Line)
                .ToList();

            yield return new ReadableBlock.Heading(level + 1, $"{TvfFenceTitle(group.Key)} ({ordered.Count})");
            yield return new ReadableBlock.Paragraph(RuleDocSite.Url(SarifRuleCatalog.TvfFenceRuleId(group.Key)));
            yield return new ReadableBlock.Table(
                [WhereHeader, "Referenced", "Fence function", "Depth", "Origin", DetailHeader],
                [.. ordered.Select(f => new List<string>
                {
                    Where(f.SourcePath, f.Line, f.DynamicSqlCallSite, pathBase, f.Confidence),
                    f.ReferencedObjectQualifiedName ?? "-",
                    f.FunctionQualifiedName is { } fn ? $"{fn} ({f.FunctionKind})" : "-",
                    f.Depth.ToString(CultureInfo.InvariantCulture),
                    f.OriginSourcePath is { } origin ? $"{Relative(origin, pathBase)}:{f.OriginLine.ToString(CultureInfo.InvariantCulture)}" : "-",
                    f.Kind == TvfFenceFindingKind.CorrelatedApply && f.CorrelatedOuterColumns is { Count: > 0 } cols
                        ? $"correlated on {string.Join(", ", cols)}"
                        : f.ReferenceFragmentText ?? "-",
                })]);
        }
    }

    private static string TvfFenceTitle(TvfFenceFindingKind kind) => kind switch
    {
        TvfFenceFindingKind.CorrelatedApply => "Correlated CROSS/OUTER APPLY (re-executes per outer row)",
        TvfFenceFindingKind.NestedUnderViewOrTvf => "Fence inherited through a view/TVF layer",
        TvfFenceFindingKind.FromOrJoin => "Direct FROM/JOIN reference",
        TvfFenceFindingKind.InsertExec => "INSERT ... EXEC (forced worktable materialization)",
        TvfFenceFindingKind.Standalone => "Standalone reference (fence present, nothing to poison)",
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "Unhandled TvfFenceFindingKind."),
    };

    private static IEnumerable<ReadableBlock> ScalarUdf(ScanReport report, int level, string? pathBase)
    {
        if (report.Find<ScalarUdfFinding>(nameof(ScalarUdfScanner)).Count == 0)
        {
            yield break;
        }

        yield return new ReadableBlock.Heading(level, $"Scalar UDF calls ({report.Find<ScalarUdfFinding>(nameof(ScalarUdfScanner)).Count})");
        yield return new ReadableBlock.Paragraph(
            "A scalar UDF executes once per row wherever it's called; pre-2019 (or on any engine when it proves non-inlineable) it also forces the whole plan serial. A predicate-context call additionally loses sargability, and a call reached through a view/iTVF's own expansion inherits the same cost invisibly at every consumer. Predicate-context and lineage-inherited calls are listed first; a call the engine itself inlines (2019+ FROID) is noted but ranked no higher than Unknown/NotInlineable ones.");

        foreach (var group in report.Find<ScalarUdfFinding>(nameof(ScalarUdfScanner))
            .GroupBy(f => f.Kind)
            .OrderBy(g => g.Key))
        {
            var ordered = group
                .OrderByDescending(f => f.Depth)
                .ThenBy(f => f.SourcePath, StringComparer.Ordinal)
                .ThenBy(f => f.Line)
                .ToList();

            yield return new ReadableBlock.Heading(level + 1, $"{ScalarUdfTitle(group.Key)} ({ordered.Count})");
            yield return new ReadableBlock.Paragraph(RuleDocSite.Url(SarifRuleCatalog.ScalarUdfRuleId(group.Key)));
            yield return new ReadableBlock.Table(
                [WhereHeader, "Function", "Context", "Inlineable", "Depth", "Origin", DetailHeader],
                [.. ordered.Select(f => new List<string>
                {
                    Where(f.SourcePath, f.Line, f.DynamicSqlCallSite, pathBase, f.Confidence),
                    $"{f.FunctionQualifiedName} ({f.UdfKind})",
                    f.Context.ToString(),
                    ScalarUdfInlineabilityDisplay(f),
                    f.Depth.ToString(CultureInfo.InvariantCulture),
                    f.OriginSourcePath is { } origin ? $"{Relative(origin, pathBase)}:{f.OriginLine.ToString(CultureInfo.InvariantCulture)}" : "-",
                    ScalarUdfDetail(f),
                })]);
        }
    }

    private static IEnumerable<ReadableBlock> ColumnCollationDrift(ScanReport report, int level, string? pathBase)
    {
        if (report.Find<ColumnCollationDriftFinding>(nameof(ColumnCollationDriftScanner)).Count == 0)
        {
            yield break;
        }

        yield return new ReadableBlock.Heading(level, $"Columns whose collation drifts from the default ({report.Find<ColumnCollationDriftFinding>(nameof(ColumnCollationDriftScanner)).Count})");
        yield return new ReadableBlock.Paragraph(
            "A conversion seed, not yet a comparison: this column's own collation differs from the database's default (or, for a temp table/table variable, from tempdb's own effective collation) - the classic setup for a future collation-conflict compile error or a forced-scan implicit conversion once a query actually compares it against something carrying the baseline collation.");

        yield return new ReadableBlock.Paragraph(RuleDocSite.Url(SarifRuleCatalog.ColumnCollationDriftRuleId));
        yield return new ReadableBlock.Table(
            [WhereHeader, "Column collation", "Baseline collation", "Object kind"],
            [.. report.Find<ColumnCollationDriftFinding>(nameof(ColumnCollationDriftScanner)).Select(f => new List<string>
            {
                Where(f.SourcePath, f.Line, dynamicSqlCallSite: null, pathBase, f.Confidence),
                f.ColumnCollationName,
                f.BaselineCollationName,
                f.IsTempObject ? "temp table/table variable" : "table",
            })]);
    }

    private static IEnumerable<ReadableBlock> AnsiPaddingOffColumn(ScanReport report, int level, string? pathBase)
    {
        if (report.Find<AnsiPaddingOffColumnFinding>(nameof(AnsiPaddingOffColumnScanner)).Count == 0)
        {
            yield break;
        }

        yield return new ReadableBlock.Heading(level, $"Columns with ANSI_PADDING OFF in their own catalog state ({report.Find<AnsiPaddingOffColumnFinding>(nameof(AnsiPaddingOffColumnScanner)).Count})");
        yield return new ReadableBlock.Paragraph(
            "A VARCHAR/NVARCHAR/VARBINARY column's own ANSI_PADDING state was fixed OFF at creation (or its last ALTER COLUMN) and stays that way regardless of any later session's own ANSI_PADDING setting - every write into it silently strips trailing blanks/zero bytes, oracle-confirmed even under a writing session with ANSI_PADDING ON.");

        yield return new ReadableBlock.Paragraph(RuleDocSite.Url(SarifRuleCatalog.ColumnAnsiPaddingOffRuleId));
        yield return new ReadableBlock.Table(
            [WhereHeader, ColumnHeader],
            [.. report.Find<AnsiPaddingOffColumnFinding>(nameof(AnsiPaddingOffColumnScanner)).Select(f => new List<string>
            {
                Where(f.SourcePath, f.Line, dynamicSqlCallSite: null, pathBase, f.Confidence),
                $"{f.TableQualifiedName}.{f.ColumnName}",
            })]);
    }

    private static IEnumerable<ReadableBlock> CrossTableTypeDrift(ScanReport report, int level, string? pathBase)
    {
        if (report.Find<CrossTableTypeDriftFinding>(nameof(CrossTableTypeDriftScanner)).Count == 0)
        {
            yield break;
        }

        yield return new ReadableBlock.Heading(level, $"Foreign-key column pairs whose types drift ({report.Find<CrossTableTypeDriftFinding>(nameof(CrossTableTypeDriftScanner)).Count})");
        yield return new ReadableBlock.Paragraph(
            "A conversion seed on a real foreign-key relationship: every JOIN that follows it risks the same column-side conversion the implicit-conversion stream classifies, whether or not any scanned query actually joins on it yet. Read live from sys.foreign_key_columns - always empty for a file-mode scan.");

        yield return new ReadableBlock.Paragraph(RuleDocSite.Url(SarifRuleCatalog.CrossTableTypeDriftRuleId));
        yield return new ReadableBlock.Table(
            [WhereHeader, ConstraintHeader, "Parent column", "Referenced column", "Collation differs"],
            [.. report.Find<CrossTableTypeDriftFinding>(nameof(CrossTableTypeDriftScanner)).Select(f => new List<string>
            {
                Where(f.SourcePath, f.Line, dynamicSqlCallSite: null, pathBase, f.Confidence),
                f.ConstraintName,
                $"{f.ParentTableQualifiedName}.{f.ParentColumnName} ({f.ParentTypeDisplay})",
                $"{f.ReferencedTableQualifiedName}.{f.ReferencedColumnName} ({f.ReferencedTypeDisplay})",
                f.CollationDiffers.ToString(),
            })]);
    }

    private static IEnumerable<ReadableBlock> TriggerOrder(ScanReport report, int level, string? pathBase)
    {
        if (report.Find<TriggerOrderFinding>(nameof(TriggerOrderScanner)).Count == 0)
        {
            yield break;
        }

        yield return new ReadableBlock.Heading(level, $"Tables with undefined trigger firing order ({report.Find<TriggerOrderFinding>(nameof(TriggerOrderScanner)).Count})");
        yield return new ReadableBlock.Paragraph(
            "Two or more enabled AFTER triggers on the same table+event with no sp_settriggerorder pin narrowing their relative order down to a single pair - the engine documents this order as undefined. Read live from sys.triggers/sys.trigger_events - always empty for a file-mode scan.");

        yield return new ReadableBlock.Paragraph(RuleDocSite.Url(SarifRuleCatalog.TriggerOrderRuleId));
        yield return new ReadableBlock.Table(
            [WhereHeader, "Event", "Unordered triggers"],
            [.. report.Find<TriggerOrderFinding>(nameof(TriggerOrderScanner)).Select(f => new List<string>
            {
                Where(f.SourcePath, f.Line, dynamicSqlCallSite: null, pathBase, f.Confidence),
                f.EventTypeDescription,
                string.Join(", ", f.UnorderedTriggerNames),
            })]);
    }

    private static IEnumerable<ReadableBlock> ProcCallArgumentMismatch(ScanReport report, int level, string? pathBase)
    {
        if (report.Find<ProcCallArgumentMismatchFinding>(nameof(ProcCallArgumentMismatchScanner)).Count == 0)
        {
            yield break;
        }

        yield return new ReadableBlock.Heading(level, $"EXEC call-site argument mismatches ({report.Find<ProcCallArgumentMismatchFinding>(nameof(ProcCallArgumentMismatchScanner)).Count})");
        yield return new ReadableBlock.Paragraph(
            "A real EXEC call site has a silent narrowing conversion at parameter marshalling, not a predicate - an assignment-shaped conversion, classified the same way an INSERT/UPDATE assignment's silent data loss is. Two distinct directions can trigger it: an input parameter whose caller-side variable's declared type risks losing information on the way in (which also primes the exact mismatched value for any comparison the callee's own body makes against it), or an OUTPUT parameter whose final value risks losing information on the way back into a narrower caller-side variable after the call returns.");

        yield return new ReadableBlock.Paragraph(RuleDocSite.Url(SarifRuleCatalog.ProcCallArgumentMismatchRuleId));
        yield return new ReadableBlock.Table(
            [WhereHeader, "Callee", ParameterHeader, "Direction", "Caller-side expression", "Caller type", "Parameter type", "Risk"],
            [.. report.Find<ProcCallArgumentMismatchFinding>(nameof(ProcCallArgumentMismatchScanner)).Select(f => new List<string>
            {
                Where(f.SourcePath, f.Line, dynamicSqlCallSite: null, pathBase, f.Confidence),
                f.CalleeQualifiedName,
                f.FormalParameterName,
                f.IsOutputWriteback ? "OUTPUT writeback (callee -> caller)" : "input (caller -> callee)",
                f.CallerExpressionDisplay,
                f.CallerTypeDisplay,
                f.FormalParameterTypeDisplay,
                DescribeWriteLossKind(f.Kind),
            })]);
    }

    private static IEnumerable<ReadableBlock> ProcCallTableValuedArgumentMismatch(ScanReport report, int level, string? pathBase)
    {
        if (report.Find<ProcCallTableValuedArgumentMismatchFinding>(nameof(ProcCallTableValuedArgumentMismatchScanner)).Count == 0)
        {
            yield break;
        }

        yield return new ReadableBlock.Heading(level, $"EXEC call-site table-valued argument mismatches ({report.Find<ProcCallTableValuedArgumentMismatchFinding>(nameof(ProcCallTableValuedArgumentMismatchScanner)).Count})");
        yield return new ReadableBlock.Paragraph(
            "A real EXEC call site passes a table-valued parameter argument whose caller-side table variable was itself populated with a literal INSERT ... VALUES row that risks silent data loss against the table type's own declared column - the same assignment-shaped conversion as a scalar EXEC argument mismatch, but at the point the table variable's row data is written, since SQL Server's own table-type identity check at the call boundary is exact and cannot silently narrow.");

        yield return new ReadableBlock.Paragraph(RuleDocSite.Url(SarifRuleCatalog.ProcCallTableValuedArgumentMismatchRuleId));
        yield return new ReadableBlock.Table(
            [WhereHeader, "Callee", ParameterHeader, "Column", "Caller-side expression", "Caller type", "Column type", "Risk"],
            [.. report.Find<ProcCallTableValuedArgumentMismatchFinding>(nameof(ProcCallTableValuedArgumentMismatchScanner)).Select(f => new List<string>
            {
                Where(f.SourcePath, f.Line, dynamicSqlCallSite: null, pathBase, f.Confidence),
                f.CalleeQualifiedName,
                f.FormalParameterName,
                f.ColumnName,
                f.CallerExpressionDisplay,
                f.CallerTypeDisplay,
                f.ColumnTypeDisplay,
                DescribeWriteLossKind(f.Kind),
            })]);
    }

    private static IEnumerable<ReadableBlock> SpExecuteSqlParameterMismatch(ScanReport report, int level, string? pathBase)
    {
        if (report.Find<SpExecuteSqlParameterMismatchFinding>(nameof(SpExecuteSqlParameterMismatchScanner)).Count == 0)
        {
            yield break;
        }

        yield return new ReadableBlock.Heading(level, $"sp_executesql call-site parameter mismatches ({report.Find<SpExecuteSqlParameterMismatchFinding>(nameof(SpExecuteSqlParameterMismatchScanner)).Count})");
        yield return new ReadableBlock.Paragraph(
            "An EXEC sp_executesql call site declares its own parameter's type in the literal parameter-definition string (the second argument) and binds a caller-side variable to it - the same silent narrowing conversion as a static EXEC call's argument mismatch, but resolved from the parameter-definition string sp_executesql itself parses, not from a catalog-declared procedure signature. Two distinct directions can trigger it: an input parameter whose caller-side variable's declared type risks losing information on the way in, or an OUTPUT parameter whose final value risks losing information on the way back into a narrower caller-side variable after the call returns.");

        yield return new ReadableBlock.Paragraph(RuleDocSite.Url(SarifRuleCatalog.SpExecuteSqlParameterMismatchRuleId));
        yield return new ReadableBlock.Table(
            [WhereHeader, ParameterHeader, "Direction", "Caller-side expression", "Caller type", "Declared parameter type", "Risk"],
            [.. report.Find<SpExecuteSqlParameterMismatchFinding>(nameof(SpExecuteSqlParameterMismatchScanner)).Select(f => new List<string>
            {
                Where(f.SourcePath, f.Line, dynamicSqlCallSite: null, pathBase, f.Confidence),
                f.ParameterName,
                f.IsOutputWriteback ? "OUTPUT writeback (callee -> caller)" : "input (caller -> callee)",
                f.CallerExpressionDisplay,
                f.CallerTypeDisplay,
                f.DeclaredParameterTypeDisplay,
                DescribeWriteLossKind(f.Kind),
            })]);
    }

    private static IEnumerable<ReadableBlock> TemporalBoundary(ScanReport report, int level, string? pathBase)
    {
        if (report.Find<TemporalBoundaryPrecisionFinding>(nameof(NonSargablePredicateScanner)).Count == 0)
        {
            yield break;
        }

        yield return new ReadableBlock.Heading(level, $"BETWEEN end-of-period boundary correctness bugs ({report.Find<TemporalBoundaryPrecisionFinding>(nameof(NonSargablePredicateScanner)).Count})");
        yield return new ReadableBlock.Paragraph(
            "A CORRECTNESS finding, not a sargability one - BETWEEN itself is perfectly sargable here. The upper bound literal has fewer fractional-second digits than the column's own declared TIME/DATETIME2/DATETIMEOFFSET precision, so rows whose value falls in that precision gap are silently excluded - oracle-confirmed directly (a DATETIME2(7) row at 23:59:59.9999999 is dropped by the classic '23:59:59.997' end-of-day literal). Rewrite as >= start AND < (start of the next period) instead, which has no precision gap to fall into.");

        yield return new ReadableBlock.Table(
            [WhereHeader, ColumnHeader, "Column scale", "Boundary literal", "Literal fractional digits"],
            [.. report.Find<TemporalBoundaryPrecisionFinding>(nameof(NonSargablePredicateScanner)).Select(f => new List<string>
            {
                Where(f.SourcePath, f.Line, dynamicSqlCallSite: null, pathBase, f.Confidence),
                $"{f.TableQualifiedName}.{f.ColumnName}",
                f.ColumnScale.ToString(CultureInfo.InvariantCulture),
                f.BoundaryLiteralText,
                f.BoundaryLiteralFractionalDigits.ToString(CultureInfo.InvariantCulture),
            })]);
    }

    private static IEnumerable<ReadableBlock> MaxTypedColumn(ScanReport report, int level, string? pathBase)
    {
        var maxLength = report.Find<MaxTypedColumnFinding>(nameof(MaxTypedColumnScanner)).Where(f => f.Kind == NonIndexableColumnFindingKind.MaxLength).ToList();
        if (maxLength.Count > 0)
        {
            yield return new ReadableBlock.Heading(level, $"MAX-typed columns ({maxLength.Count})");
            yield return new ReadableBlock.Paragraph(
                "A structural catalog fact, not a comparison: VARCHAR(MAX)/NVARCHAR(MAX)/VARBINARY(MAX) columns can never be an index key column at all (SQL Server rejects them at CREATE INDEX time), so no predicate or join on them can ever seek, regardless of how they're used.");
            yield return new ReadableBlock.Paragraph(RuleDocSite.Url(SarifRuleCatalog.MaxTypedColumnRuleId(NonIndexableColumnFindingKind.MaxLength)));

            yield return new ReadableBlock.Table(
                [WhereHeader, ColumnHeader, "Type"],
                [.. maxLength.Select(f => new List<string>
                {
                    Where(f.SourcePath, f.Line, dynamicSqlCallSite: null, pathBase, f.Confidence),
                    $"{f.TableQualifiedName}.{f.ColumnName}",
                    f.TypeDisplay,
                })]);
        }

        var legacyLob = report.Find<MaxTypedColumnFinding>(nameof(MaxTypedColumnScanner)).Where(f => f.Kind == NonIndexableColumnFindingKind.LegacyLargeObject).ToList();
        if (legacyLob.Count > 0)
        {
            yield return new ReadableBlock.Heading(level, $"Legacy large-object columns ({legacyLob.Count})");
            yield return new ReadableBlock.Paragraph(
                "A structural catalog fact, not a comparison: TEXT/NTEXT/IMAGE columns can never appear in any index at all (SQL Server rejects them at CREATE INDEX time, even as a nonclustered index's INCLUDE column), so no predicate or join on them can ever seek and they can never be covered.");
            yield return new ReadableBlock.Paragraph(RuleDocSite.Url(SarifRuleCatalog.MaxTypedColumnRuleId(NonIndexableColumnFindingKind.LegacyLargeObject)));

            yield return new ReadableBlock.Table(
                [WhereHeader, ColumnHeader, "Type"],
                [.. legacyLob.Select(f => new List<string>
                {
                    Where(f.SourcePath, f.Line, dynamicSqlCallSite: null, pathBase, f.Confidence),
                    $"{f.TableQualifiedName}.{f.ColumnName}",
                    f.TypeDisplay,
                })]);
        }
    }

    private static IEnumerable<ReadableBlock> ColumnstoreUnsupportedColumnType(ScanReport report, int level, string? pathBase)
    {
        if (report.Find<ColumnstoreUnsupportedColumnTypeFinding>(nameof(ColumnstoreUnsupportedColumnTypeScanner)).Count == 0)
        {
            yield break;
        }

        yield return new ReadableBlock.Heading(level, $"Columnstore-unsupported-type columns in a columnstore index ({report.Find<ColumnstoreUnsupportedColumnTypeFinding>(nameof(ColumnstoreUnsupportedColumnTypeScanner)).Count})");
        yield return new ReadableBlock.Paragraph(
            "A structural catalog fact, not a plan-shape claim: a column of this type participating in a columnstore index does not deploy at all - oracle-confirmed real DDL execution fails with Msg 35343 (\"a data type that cannot participate in a columnstore index\").");

        yield return new ReadableBlock.Paragraph(RuleDocSite.Url(SarifRuleCatalog.ColumnstoreUnsupportedColumnTypeRuleId));
        yield return new ReadableBlock.Table(
            [WhereHeader, ColumnHeader, "Type", "Index"],
            [.. report.Find<ColumnstoreUnsupportedColumnTypeFinding>(nameof(ColumnstoreUnsupportedColumnTypeScanner)).Select(f => new List<string>
            {
                Where(f.SourcePath, f.Line, dynamicSqlCallSite: null, pathBase, f.Confidence),
                $"{f.TableQualifiedName}.{f.ColumnName}",
                f.TypeDisplay,
                f.IndexName,
            })]);
    }

    private static IEnumerable<ReadableBlock> SelectiveXmlIndexValueColumn(ScanReport report, int level, string? pathBase)
    {
        if (report.Find<SelectiveXmlIndexValueColumnFinding>(nameof(SelectiveXmlIndexValueColumnScanner)).Count == 0)
        {
            yield break;
        }

        yield return new ReadableBlock.Heading(level, $"Secondary selective XML indexes over an oversized/large-object value column ({report.Find<SelectiveXmlIndexValueColumnFinding>(nameof(SelectiveXmlIndexValueColumnScanner)).Count})");
        yield return new ReadableBlock.Paragraph(
            "A structural catalog fact, not a plan-shape claim: a secondary selective XML index over a promoted path whose declared type is a large object or wider than 900 bytes does not deploy at all - oracle-confirmed real DDL execution fails with Msg 6391 (large object) or Msg 6395 (maximum key length is 900 bytes).");

        foreach (var group in report.Find<SelectiveXmlIndexValueColumnFinding>(nameof(SelectiveXmlIndexValueColumnScanner)).GroupBy(f => f.Kind).OrderBy(g => g.Key))
        {
            var ordered = group.ToList();
            yield return new ReadableBlock.Heading(level + 1, $"{HumanizeKindName(group.Key.ToString())} ({ordered.Count})");
            yield return new ReadableBlock.Paragraph(RuleDocSite.Url(SarifRuleCatalog.SelectiveXmlIndexValueColumnRuleId(group.Key)));
            yield return new ReadableBlock.Table(
                [WhereHeader, "Secondary index", "Primary index", "Path", "Type"],
                [.. ordered.Select(f => new List<string>
                {
                    Where(f.SourcePath, f.Line, dynamicSqlCallSite: null, pathBase, f.Confidence),
                    $"{f.TableQualifiedName}.{f.SecondaryIndexName}",
                    f.PrimaryIndexName,
                    f.PathName,
                    f.TypeDisplay,
                })]);
        }
    }

    private static IEnumerable<ReadableBlock> MemoryOptimizedUnsupportedColumnType(ScanReport report, int level, string? pathBase)
    {
        if (report.Find<MemoryOptimizedUnsupportedColumnTypeFinding>(nameof(MemoryOptimizedUnsupportedColumnTypeScanner)).Count == 0)
        {
            yield break;
        }

        yield return new ReadableBlock.Heading(level, $"Unsupported column type on a memory-optimized table ({report.Find<MemoryOptimizedUnsupportedColumnTypeFinding>(nameof(MemoryOptimizedUnsupportedColumnTypeScanner)).Count})");
        yield return new ReadableBlock.Paragraph(
            "A structural catalog fact, not a plan-shape claim: xml, sql_variant, text, ntext, image, and timestamp/rowversion columns are not supported on a memory-optimized table at all - oracle-confirmed real DDL execution fails with Msg 10794.");

        yield return new ReadableBlock.Paragraph(RuleDocSite.Url(SarifRuleCatalog.MemoryOptimizedUnsupportedColumnTypeRuleId));
        yield return new ReadableBlock.Table(
            [WhereHeader, ColumnHeader, "Type"],
            [.. report.Find<MemoryOptimizedUnsupportedColumnTypeFinding>(nameof(MemoryOptimizedUnsupportedColumnTypeScanner)).Select(f => new List<string>
            {
                Where(f.SourcePath, f.Line, dynamicSqlCallSite: null, pathBase, f.Confidence),
                $"{f.TableQualifiedName}.{f.ColumnName}",
                f.TypeDisplay,
            })]);
    }

    private static IEnumerable<ReadableBlock> MemoryOptimizedUnsupportedIndexOption(ScanReport report, int level, string? pathBase)
    {
        if (report.Find<MemoryOptimizedUnsupportedIndexOptionFinding>(nameof(MemoryOptimizedUnsupportedIndexOptionScanner)).Count == 0)
        {
            yield break;
        }

        yield return new ReadableBlock.Heading(level, $"Unsupported index option on a memory-optimized table ({report.Find<MemoryOptimizedUnsupportedIndexOptionFinding>(nameof(MemoryOptimizedUnsupportedIndexOptionScanner)).Count})");
        yield return new ReadableBlock.Paragraph(
            "A structural catalog fact: a rowstore CLUSTERED index, an index with INCLUDE columns, or a filtered index (a WHERE clause on the index) is not supported on a memory-optimized table - oracle-confirmed real DDL execution fails (Msg 12317/10664/10794 respectively).");

        foreach (var group in report.Find<MemoryOptimizedUnsupportedIndexOptionFinding>(nameof(MemoryOptimizedUnsupportedIndexOptionScanner)).GroupBy(f => f.Kind).OrderBy(g => g.Key))
        {
            var ordered = group.ToList();
            yield return new ReadableBlock.Heading(level + 1, $"{MemoryOptimizedUnsupportedIndexOptionTitle(group.Key)} ({ordered.Count})");
            yield return new ReadableBlock.Paragraph(RuleDocSite.Url(SarifRuleCatalog.MemoryOptimizedUnsupportedIndexOptionRuleId(group.Key)));
            yield return new ReadableBlock.Table(
                [WhereHeader, "Table", "Index"],
                [.. ordered.Select(f => new List<string>
                {
                    Where(f.SourcePath, f.Line, dynamicSqlCallSite: null, pathBase, f.Confidence),
                    f.TableQualifiedName,
                    f.IndexName,
                })]);
        }
    }

    private static string MemoryOptimizedUnsupportedIndexOptionTitle(MemoryOptimizedUnsupportedIndexOptionKind kind) => kind switch
    {
        MemoryOptimizedUnsupportedIndexOptionKind.ClusteredIndex => "Rowstore CLUSTERED index",
        MemoryOptimizedUnsupportedIndexOptionKind.IncludedColumns => "INCLUDE columns",
        MemoryOptimizedUnsupportedIndexOptionKind.FilteredIndex => "Filtered index (WHERE clause)",
        _ => "Unsupported index option",
    };

    private static IEnumerable<ReadableBlock> MemoryOptimizedForeignKey(ScanReport report, int level, string? pathBase)
    {
        if (report.Find<MemoryOptimizedForeignKeyFinding>(nameof(MemoryOptimizedForeignKeyScanner)).Count == 0)
        {
            yield break;
        }

        yield return new ReadableBlock.Heading(level, $"Unsupported memory-optimized foreign key ({report.Find<MemoryOptimizedForeignKeyFinding>(nameof(MemoryOptimizedForeignKeyScanner)).Count})");
        yield return new ReadableBlock.Paragraph(
            "A structural catalog fact: a foreign key spanning a memory-optimized and a disk-based table, or a CASCADE/SET NULL/SET DEFAULT referential action between two memory-optimized tables, is not supported - oracle-confirmed real DDL execution fails (Msg 10778/10794 respectively).");

        foreach (var group in report.Find<MemoryOptimizedForeignKeyFinding>(nameof(MemoryOptimizedForeignKeyScanner)).GroupBy(f => f.Kind).OrderBy(g => g.Key))
        {
            var ordered = group.ToList();
            yield return new ReadableBlock.Heading(level + 1, $"{MemoryOptimizedForeignKeyTitle(group.Key)} ({ordered.Count})");
            yield return new ReadableBlock.Paragraph(RuleDocSite.Url(SarifRuleCatalog.MemoryOptimizedForeignKeyRuleId(group.Key)));
            yield return new ReadableBlock.Table(
                [WhereHeader, "Constraint", "Parent table", "Referenced table"],
                [.. ordered.Select(f => new List<string>
                {
                    Where(f.SourcePath, f.Line, dynamicSqlCallSite: null, pathBase, f.Confidence),
                    f.ConstraintName,
                    f.ParentTableQualifiedName,
                    f.ReferencedTableQualifiedName,
                })]);
        }
    }

    private static string MemoryOptimizedForeignKeyTitle(MemoryOptimizedForeignKeyFindingKind kind) => kind switch
    {
        MemoryOptimizedForeignKeyFindingKind.CrossStorageForeignKey => "Spans memory-optimized and disk-based tables",
        MemoryOptimizedForeignKeyFindingKind.ReferentialAction => "Non-NO ACTION referential action",
        _ => "Unsupported foreign key shape",
    };

    private static IEnumerable<ReadableBlock> MemoryOptimizedSchemaOnlyDurability(ScanReport report, int level, string? pathBase)
    {
        if (report.Find<MemoryOptimizedSchemaOnlyDurabilityFinding>(nameof(MemoryOptimizedSchemaOnlyDurabilityScanner)).Count == 0)
        {
            yield break;
        }

        yield return new ReadableBlock.Heading(level, $"Memory-optimized table declared SCHEMA_ONLY durability ({report.Find<MemoryOptimizedSchemaOnlyDurabilityFinding>(nameof(MemoryOptimizedSchemaOnlyDurabilityScanner)).Count})");
        yield return new ReadableBlock.Paragraph(
            "A structural catalog fact: DURABILITY = SCHEMA_ONLY persists only the table's schema, not its rows - oracle-confirmed every row is lost on a server restart, failover, or database restore/attach, with no error or warning.");

        yield return new ReadableBlock.Paragraph(RuleDocSite.Url(SarifRuleCatalog.MemoryOptimizedSchemaOnlyDurabilityRuleId));
        yield return new ReadableBlock.Table(
            [WhereHeader, "Table"],
            [.. report.Find<MemoryOptimizedSchemaOnlyDurabilityFinding>(nameof(MemoryOptimizedSchemaOnlyDurabilityScanner)).Select(f => new List<string>
            {
                Where(f.SourcePath, f.Line, dynamicSqlCallSite: null, pathBase, f.Confidence),
                f.TableQualifiedName,
            })]);
    }

    private static IEnumerable<ReadableBlock> NonPersistedComputedColumn(ScanReport report, int level, string? pathBase)
    {
        if (report.Find<NonPersistedComputedColumnFinding>(nameof(NonPersistedComputedColumnScanner)).Count == 0)
        {
            yield break;
        }

        yield return new ReadableBlock.Heading(level, $"Non-persisted computed columns ({report.Find<NonPersistedComputedColumnFinding>(nameof(NonPersistedComputedColumnScanner)).Count})");
        yield return new ReadableBlock.Paragraph(
            "A structural catalog fact (sys.computed_columns.is_persisted = 0): the column's definition is recomputed from the base row on every read served from the base table, independent of whether that definition calls a UDF - never fires on a PERSISTED computed column, regardless of whether it's also indexed. When an index already stores the column's value, reads served through that specific index avoid the recompute; reads that don't use it still pay it.");

        yield return new ReadableBlock.Paragraph(RuleDocSite.Url(SarifRuleCatalog.NonPersistedComputedColumnRuleId));
        yield return new ReadableBlock.Table(
            [WhereHeader, ColumnHeader, "Definition", "Covered by an index"],
            [.. report.Find<NonPersistedComputedColumnFinding>(nameof(NonPersistedComputedColumnScanner)).Select(f => new List<string>
            {
                Where(f.SourcePath, f.Line, dynamicSqlCallSite: null, pathBase, f.Confidence),
                $"{f.TableQualifiedName}.{f.ColumnName}",
                f.DefinitionText,
                f.IsCoveredByIndex ? "Yes" : "No",
            })]);
    }

    private static IEnumerable<ReadableBlock> OversizedParameter(ScanReport report, int level, string? pathBase)
    {
        if (report.Find<OversizedParameterFinding>(nameof(TypedPredicateExtractor)).Count == 0)
        {
            yield break;
        }

        yield return new ReadableBlock.Heading(level, $"Predicates comparing a column against an oversized parameter ({report.Find<OversizedParameterFinding>(nameof(TypedPredicateExtractor)).Count})");
        yield return new ReadableBlock.Paragraph(
            "Informational, not a plan-shape claim for this specific predicate - oracle-falsified that a bare equality predicate shows any memory-grant difference on its own. The risk is structural: the parameter/variable/expression on the other side is declared with a meaningfully longer length than the column, which risks memory-grant inflation once that value feeds a sort/hash operator elsewhere in the plan.");

        yield return new ReadableBlock.Paragraph(RuleDocSite.Url(SarifRuleCatalog.OversizedParameterRuleId));
        yield return new ReadableBlock.Table(
            [WhereHeader, ColumnHeader, "Column length", "Other operand length"],
            [.. report.Find<OversizedParameterFinding>(nameof(TypedPredicateExtractor)).Select(f => new List<string>
            {
                Where(f.SourcePath, f.Line, dynamicSqlCallSite: null, pathBase, f.Confidence),
                $"{f.TableQualifiedName}.{f.ColumnName}",
                f.ColumnLength.ToString(CultureInfo.InvariantCulture),
                f.OtherOperandLength.ToString(CultureInfo.InvariantCulture),
            })]);
    }

    private static IEnumerable<ReadableBlock> UnderLengthParameter(ScanReport report, int level, string? pathBase)
    {
        if (report.Find<UnderLengthParameterFinding>(nameof(TypedPredicateExtractor)).Count == 0)
        {
            yield break;
        }

        yield return new ReadableBlock.Heading(level, $"Predicates comparing a column against an under-length parameter ({report.Find<UnderLengthParameterFinding>(nameof(TypedPredicateExtractor)).Count})");
        yield return new ReadableBlock.Paragraph(
            "The mirror of the oversized-parameter section above, but strictly worse: the parameter/variable/expression on the other side is declared SHORTER than the column - or with no explicit length at all (T-SQL defaults a length-less DECLARE/parameter to 1) - so the value is silently truncated before the predicate ever runs. Structural, not a per-instance proof (this pass never traces the variable's actual assigned value): it states the declared-length pairing risks truncation, the same honesty WriteLossFinding already applies to assignment-site truncation. Where the parameter feeds a LIKE pattern or a range bound, truncation changes what the comparison itself means, not just which exact value it excludes - marked in the Effect column.");

        yield return new ReadableBlock.Paragraph(RuleDocSite.Url(SarifRuleCatalog.UnderLengthParameterRuleId));
        yield return new ReadableBlock.Table(
            [WhereHeader, ColumnHeader, "Column length", "Other operand length", OperatorHeader, "Effect"],
            [.. report.Find<UnderLengthParameterFinding>(nameof(TypedPredicateExtractor)).Select(f => new List<string>
            {
                Where(f.SourcePath, f.Line, dynamicSqlCallSite: null, pathBase, f.Confidence),
                $"{f.TableQualifiedName}.{f.ColumnName}",
                f.ColumnLength.ToString(CultureInfo.InvariantCulture),
                f.IsImplicitDefault ? "none (defaults to 1)" : f.OtherOperandLength!.Value.ToString(CultureInfo.InvariantCulture),
                f.Operator,
                f.ChangesRangeOrPatternShape ? "changes pattern/range shape" : "truncates compared value",
            })]);
    }

    private static IEnumerable<ReadableBlock> AnsiPaddingMismatch(ScanReport report, int level, string? pathBase)
    {
        if (report.Find<AnsiPaddingMismatchFinding>(nameof(TypedPredicateExtractor)).Count == 0)
        {
            yield break;
        }

        yield return new ReadableBlock.Heading(level, $"LIKE predicates that can never match a non-ANSI-padded column ({report.Find<AnsiPaddingMismatchFinding>(nameof(TypedPredicateExtractor)).Count})");
        yield return new ReadableBlock.Paragraph(
            "A CORRECTNESS finding, not a plan-shape one: the column's own catalog flag (sys.columns.is_ansi_padded = 0) means trailing blanks are stripped at INSERT time, so the column can never store a value ending in whitespace at all. The LIKE pattern here has significant trailing whitespace, so this predicate can never match anything the column could ever contain - oracle-confirmed directly (real seeded rows) that a plain equality comparison is NOT affected the same way, since T-SQL trims trailing spaces for '=' regardless of padding; only LIKE, where a pattern's own trailing whitespace is never trimmed, shows the real difference.");

        yield return new ReadableBlock.Paragraph(RuleDocSite.Url(SarifRuleCatalog.AnsiPaddingMismatchRuleId));
        yield return new ReadableBlock.Table(
            [WhereHeader, ColumnHeader, "Pattern"],
            [.. report.Find<AnsiPaddingMismatchFinding>(nameof(TypedPredicateExtractor)).Select(f => new List<string>
            {
                Where(f.SourcePath, f.Line, dynamicSqlCallSite: null, pathBase, f.Confidence),
                $"{f.TableQualifiedName}.{f.ColumnName}",
                f.PatternLiteralText,
            })]);
    }

    private static IEnumerable<ReadableBlock> CatchAllPredicate(ScanReport report, int level, string? pathBase)
    {
        if (report.Find<CatchAllPredicateFinding>(nameof(CatchAllPredicateScanner)).Count == 0)
        {
            yield break;
        }

        yield return new ReadableBlock.Heading(level, $"Catch-all / kitchen-sink predicates ({report.Find<CatchAllPredicateFinding>(nameof(CatchAllPredicateScanner)).Count})");
        yield return new ReadableBlock.Paragraph(
            "The classic '(Col = @p OR @p IS NULL)' optional-filter idiom (Erland Sommarskog, \"Dynamic Search Conditions in T-SQL\") - one cached plan must stay correct for every NULL/non-NULL state of @p, typically forcing a scan regardless of what value a given call actually passes. Not a claim about what a specific already-compiled plan is doing right now - a structural risk report. Suppressed entirely (not merely downgraded) when the statement carries OPTION (RECOMPILE) or the procedure is WITH RECOMPILE, both of which let the optimizer see the real value on each call and fully resolve this risk. Rows on a confirmed-indexed column are listed first.");

        yield return new ReadableBlock.Paragraph(RuleDocSite.Url(SarifRuleCatalog.CatchAllPredicateRuleId));
        yield return new ReadableBlock.Table(
            [WhereHeader, ColumnHeader, ParameterHeader, IndexedHeader],
            [.. report.Find<CatchAllPredicateFinding>(nameof(CatchAllPredicateScanner))
                .OrderByDescending(f => f.Indexed)
                .ThenBy(f => f.SourcePath, StringComparer.Ordinal)
                .ThenBy(f => f.Line)
                .Select(f => new List<string>
            {
                Where(f.SourcePath, f.Line, dynamicSqlCallSite: null, pathBase, f.Confidence),
                $"{f.TableQualifiedName}.{f.ColumnName}",
                f.ParameterName,
                f.Indexed ? "yes" : "no",
            })]);
    }

    private static IEnumerable<ReadableBlock> LocalVariablePredicate(ScanReport report, int level, string? pathBase)
    {
        if (report.Find<LocalVariablePredicateFinding>(nameof(TypedPredicateExtractor)).Count == 0)
        {
            yield break;
        }

        yield return new ReadableBlock.Heading(level, $"Predicates against a local variable, not a parameter ({report.Find<LocalVariablePredicateFinding>(nameof(TypedPredicateExtractor)).Count})");
        yield return new ReadableBlock.Paragraph(
            "Purely informational, not a sargability claim: the predicate is still fully sargable and WILL seek if the column is indexed. The compared value came from a DECLARE'd local variable, not a formal parameter, so it is invisible to the cardinality estimator (Microsoft's own documented behavior - the optimizer falls back to the column's average-density statistic instead of a value-specific estimate). Whether a bad estimate actually matters depends on data-distribution facts this pass cannot see - listed for awareness, not as a proven defect. Suppressed entirely when the statement carries OPTION (RECOMPILE) or the procedure is WITH RECOMPILE, since a per-execution recompile lets the optimizer see the variable's real current value.");

        yield return new ReadableBlock.Paragraph(RuleDocSite.Url(SarifRuleCatalog.LocalVariablePredicateRuleId));
        yield return new ReadableBlock.Table(
            [WhereHeader, ColumnHeader, "Variable", OperatorHeader, IndexedHeader],
            [.. report.Find<LocalVariablePredicateFinding>(nameof(TypedPredicateExtractor)).Select(f => new List<string>
            {
                Where(f.SourcePath, f.Line, dynamicSqlCallSite: null, pathBase, f.Confidence),
                $"{f.TableQualifiedName}.{f.ColumnName}",
                f.VariableName,
                f.Operator,
                f.Indexed switch { true => "yes", false => "no", null => "unresolved" },
            })]);
    }

    private static IEnumerable<ReadableBlock> FilteredIndexParameterMismatch(ScanReport report, int level, string? pathBase)
    {
        if (report.Find<FilteredIndexParameterMismatchFinding>(nameof(TypedPredicateExtractor)).Count == 0)
        {
            yield break;
        }

        yield return new ReadableBlock.Heading(level, $"Filtered index matched only against a literal, but the query uses a parameter/variable ({report.Find<FilteredIndexParameterMismatchFinding>(nameof(TypedPredicateExtractor)).Count})");
        yield return new ReadableBlock.Paragraph(
            "A real access-path defect, oracle-confirmed (SET SHOWPLAN_XML, 2026-08-18): the optimizer can only match a filtered index against a query whose own WHERE clause restates the filter with a LITERAL value - a query filtering the same column via a parameter or local variable can never use that index, even when the runtime value is identical to the index's own filter literal. Not a cardinality-estimate risk like a plain local-variable predicate; the access path itself is unavailable. Not suppressed by OPTION (RECOMPILE)/WITH RECOMPILE - confirmed directly that a recompiled plan still cannot match the index, since the limitation is evaluated against the predicate's compile-time shape, not its runtime value.");

        yield return new ReadableBlock.Paragraph(RuleDocSite.Url(SarifRuleCatalog.FilteredIndexParameterMismatchRuleId));
        yield return new ReadableBlock.Table(
            [WhereHeader, ColumnHeader, "Filtered index", "Filter literal", "Variable", OperatorHeader],
            [.. report.Find<FilteredIndexParameterMismatchFinding>(nameof(TypedPredicateExtractor)).Select(f => new List<string>
            {
                Where(f.SourcePath, f.Line, dynamicSqlCallSite: null, pathBase, f.Confidence),
                $"{f.TableQualifiedName}.{f.ColumnName}",
                f.IndexName ?? "<unnamed>",
                f.FilterLiteralText,
                f.VariableName,
                f.Operator,
            })]);
    }

    private static IEnumerable<ReadableBlock> ParameterReassignmentPredicate(ScanReport report, int level, string? pathBase)
    {
        if (report.Find<ParameterReassignmentPredicateFinding>(nameof(ParameterReassignmentPredicateScanner)).Count == 0)
        {
            yield break;
        }

        yield return new ReadableBlock.Heading(level, $"Predicates against a reassigned formal parameter ({report.Find<ParameterReassignmentPredicateFinding>(nameof(ParameterReassignmentPredicateScanner)).Count})");
        yield return new ReadableBlock.Paragraph(
            "Purely informational, not a sargability claim: the predicate is still fully sargable and WILL seek if the column is indexed. The compared value is a formal parameter that is reassigned (SET/SELECT) on every statically reachable path before this predicate runs - the optimizer's compile-time sniffed value (the caller's original argument) is provably stale by the time this comparison executes. Distinct from a predicate against a plain DECLARE'd local variable (never sniffable to begin with) - here a value that WAS sniffable had its sniffed value invalidated by the procedure's own code. Suppressed entirely when the statement carries OPTION (RECOMPILE) or the procedure is WITH RECOMPILE.");

        yield return new ReadableBlock.Paragraph(RuleDocSite.Url(SarifRuleCatalog.ParameterReassignmentPredicateRuleId));
        yield return new ReadableBlock.Table(
            [WhereHeader, ColumnHeader, ParameterHeader, OperatorHeader, IndexedHeader, "Reassigned at"],
            [.. report.Find<ParameterReassignmentPredicateFinding>(nameof(ParameterReassignmentPredicateScanner)).Select(f => new List<string>
            {
                Where(f.SourcePath, f.Line, dynamicSqlCallSite: null, pathBase, f.Confidence),
                $"{f.TableQualifiedName}.{f.ColumnName}",
                f.ParameterName,
                f.Operator,
                f.Indexed ? "yes" : "no",
                $"line {f.ReassignmentLine}",
            })]);
    }

    private static IEnumerable<ReadableBlock> CodeMetric(ScanReport report, int level, string? pathBase)
    {
        if (report.Find<CodeMetricFinding>(nameof(CodeMetricScanner)).Count == 0)
        {
            yield break;
        }

        yield return new ReadableBlock.Heading(level, $"Size/complexity metric thresholds exceeded ({report.Find<CodeMetricFinding>(nameof(CodeMetricScanner)).Count})");
        yield return new ReadableBlock.Paragraph(
            "Purely a maintainability/readability signal - none of these eight metrics change a query's result or its plan. Every threshold is configurable; the defaults were calibrated against this codebase's own real corpus distribution, not invented arbitrarily.");

        foreach (var group in report.Find<CodeMetricFinding>(nameof(CodeMetricScanner)).GroupBy(f => f.Kind).OrderBy(g => g.Key))
        {
            var ordered = group.ToList();
            yield return new ReadableBlock.Heading(level + 1, $"{HumanizeKindName(group.Key.ToString())} ({ordered.Count})");
            yield return new ReadableBlock.Paragraph(RuleDocSite.Url(SarifRuleCatalog.CodeMetricRuleId(group.Key)));
            yield return new ReadableBlock.Table(
                [WhereHeader, "Measured", "Threshold", DetailHeader],
                [.. ordered.Select(f => new List<string>
                {
                    Where(f.SourcePath, f.Line, dynamicSqlCallSite: null, pathBase, f.Confidence),
                    f.MeasuredValue.ToString(CultureInfo.InvariantCulture),
                    f.Threshold.ToString(CultureInfo.InvariantCulture),
                    f.DetailText ?? f.ModuleQualifiedName,
                })]);
        }
    }

    private static IEnumerable<ReadableBlock> Formatting(ScanReport report, int level, string? pathBase)
    {
        if (report.Find<FormattingFinding>(nameof(FormattingScanner)).Count == 0)
        {
            yield break;
        }

        yield return new ReadableBlock.Heading(level, $"Formatting and layout risks ({report.Find<FormattingFinding>(nameof(FormattingScanner)).Count})");
        yield return new ReadableBlock.Paragraph(
            "Purely a readability/maintainability signal for most of these - none change a query's result or its plan. Two kinds are a visual-ambiguity risk instead (a statement that looks like it belongs to a conditional/loop but structurally does not): the statement's own behavior is still unaffected, only a future edit relying on the misleading shape is at risk.");

        foreach (var group in report.Find<FormattingFinding>(nameof(FormattingScanner)).GroupBy(f => f.Kind).OrderBy(g => g.Key))
        {
            var ordered = group.ToList();
            yield return new ReadableBlock.Heading(level + 1, $"{HumanizeKindName(group.Key.ToString())} ({ordered.Count})");
            yield return new ReadableBlock.Paragraph(RuleDocSite.Url(SarifRuleCatalog.FormattingRuleId(group.Key)));
            yield return new ReadableBlock.Table(
                [WhereHeader, DetailHeader],
                [.. ordered.Select(f => new List<string>
                {
                    Where(f.SourcePath, f.Line, dynamicSqlCallSite: null, pathBase, f.Confidence),
                    f.DetailText ?? f.ModuleQualifiedName,
                })]);
        }
    }

    private static IEnumerable<ReadableBlock> Naming(ScanReport report, int level, string? pathBase)
    {
        if (report.Find<NamingFinding>(nameof(NamingScanner)).Count == 0)
        {
            yield break;
        }

        yield return new ReadableBlock.Heading(level, $"Naming and identifier risks ({report.Find<NamingFinding>(nameof(NamingScanner)).Count})");
        yield return new ReadableBlock.Paragraph(
            "A reserved keyword used as an identifier, a user-defined procedure/function named with the \"sp_\" prefix, a schema-scoped CREATE with no explicit schema qualifier, and a redundant \"dbo.\" qualifier on a type reference.");

        foreach (var group in report.Find<NamingFinding>(nameof(NamingScanner)).GroupBy(f => f.Kind).OrderBy(g => g.Key))
        {
            var ordered = group.ToList();
            yield return new ReadableBlock.Heading(level + 1, $"{HumanizeKindName(group.Key.ToString())} ({ordered.Count})");
            yield return new ReadableBlock.Paragraph(RuleDocSite.Url(SarifRuleCatalog.NamingRuleId(group.Key)));
            yield return new ReadableBlock.Table(
                [WhereHeader, DetailHeader],
                [.. ordered.Select(f => new List<string>
                {
                    Where(f.SourcePath, f.Line, dynamicSqlCallSite: null, pathBase, f.Confidence),
                    f.DetailText,
                })]);
        }
    }

    private static IEnumerable<ReadableBlock> ForcedParameterization(ScanReport report, int level, string? pathBase)
    {
        if (report.Find<ForcedParameterizationFinding>(nameof(ForcedParameterizationScanner)).Count == 0)
        {
            yield break;
        }

        yield return new ReadableBlock.Heading(level, $"Forced-parameterization-defeating query shapes ({report.Find<ForcedParameterizationFinding>(nameof(ForcedParameterizationScanner)).Count})");
        yield return new ReadableBlock.Paragraph(
            "Live-mode only, reported only when the target database has PARAMETERIZATION FORCED on. Ten query-text clause shapes - a LIKE pattern, a TOP/OFFSET-FETCH row count, a select-list/HAVING/ORDER-BY/OUTPUT-clause literal, a TABLESAMPLE size, a literal argument to a TypeName::Method(...) static call/CONVERT style code/CHECKSUM(...), and a constant-foldable arithmetic expression - each independently oracle-confirmed (docs/detection-reference.md Appendix 8) to stay unparameterized even while the rest of the same statement correctly shares one plan, silently defeating the setting for exactly the values an app's own workload varies most.");

        foreach (var group in report.Find<ForcedParameterizationFinding>(nameof(ForcedParameterizationScanner)).GroupBy(f => f.Kind).OrderBy(g => g.Key))
        {
            var ordered = group.ToList();
            yield return new ReadableBlock.Heading(level + 1, $"{HumanizeKindName(group.Key.ToString())} ({ordered.Count})");
            yield return new ReadableBlock.Paragraph(RuleDocSite.Url(SarifRuleCatalog.ForcedParameterizationRuleId(group.Key)));
            yield return new ReadableBlock.Table(
                [WhereHeader, DetailHeader],
                [.. ordered.Select(f => new List<string>
                {
                    Where(f.SourcePath, f.Line, dynamicSqlCallSite: null, pathBase, f.Confidence),
                    f.DetailText,
                })]);
        }
    }

    private static IEnumerable<ReadableBlock> DeadCode(ScanReport report, int level, string? pathBase)
    {
        if (report.Find<DeadCodeFinding>(nameof(DeadCodeScanner)).Count == 0)
        {
            yield break;
        }

        yield return new ReadableBlock.Heading(level, $"Dead code and control-flow risks ({report.Find<DeadCodeFinding>(nameof(DeadCodeScanner)).Count})");
        yield return new ReadableBlock.Paragraph(
            "Unreachable code, an unused label, an unused local variable, an unused non-OUTPUT parameter, or a GOTO whose target is the very next statement. Purely a maintainability signal for every kind - the flagged code's own current behavior is unaffected.");

        foreach (var group in report.Find<DeadCodeFinding>(nameof(DeadCodeScanner)).GroupBy(f => f.Kind).OrderBy(g => g.Key))
        {
            var ordered = group.ToList();
            yield return new ReadableBlock.Heading(level + 1, $"{HumanizeKindName(group.Key.ToString())} ({ordered.Count})");
            yield return new ReadableBlock.Paragraph(RuleDocSite.Url(SarifRuleCatalog.DeadCodeRuleId(group.Key)));
            yield return new ReadableBlock.Table(
                [WhereHeader, DetailHeader],
                [.. ordered.Select(f => new List<string>
                {
                    Where(f.SourcePath, f.Line, dynamicSqlCallSite: null, pathBase, f.Confidence),
                    f.DetailText ?? f.ModuleQualifiedName,
                })]);
        }
    }

    private static IEnumerable<ReadableBlock> Duplication(ScanReport report, int level, string? pathBase)
    {
        if (report.Find<DuplicationFinding>(nameof(DuplicationScanner)).Count == 0)
        {
            yield break;
        }

        yield return new ReadableBlock.Heading(level, $"Duplicated/redundant code shapes ({report.Find<DuplicationFinding>(nameof(DuplicationScanner)).Count})");
        yield return new ReadableBlock.Paragraph(
            "Commented-out code, a duplicated string literal, a WHILE loop that can only run once, a self-assignment, identical operands either side of an operator, a repeated unary operator, a negated comparison written as the negation of its opposite, a duplicated or all-identical conditional branch, a redundant or mutually-exclusive AND-combined numeric bound, a collapsible nested IF, a nested IIF, or an always-true/always-false literal comparison. Purely a maintainability/readability signal for every kind - the flagged code's own current behavior is unaffected.");

        foreach (var group in report.Find<DuplicationFinding>(nameof(DuplicationScanner)).GroupBy(f => f.Kind).OrderBy(g => g.Key))
        {
            var ordered = group.ToList();
            yield return new ReadableBlock.Heading(level + 1, $"{HumanizeKindName(group.Key.ToString())} ({ordered.Count})");
            yield return new ReadableBlock.Paragraph(RuleDocSite.Url(SarifRuleCatalog.DuplicationRuleId(group.Key)));
            yield return new ReadableBlock.Table(
                [WhereHeader, DetailHeader],
                [.. ordered.Select(f => new List<string>
                {
                    Where(f.SourcePath, f.Line, dynamicSqlCallSite: null, pathBase, f.Confidence),
                    f.DetailText ?? f.ModuleQualifiedName,
                })]);
        }
    }

    private static IEnumerable<ReadableBlock> DeprecatedSyntax(ScanReport report, int level, string? pathBase)
    {
        if (report.Find<DeprecatedSyntaxFinding>(nameof(DeprecatedSyntaxScanner)).Count == 0)
        {
            yield break;
        }

        yield return new ReadableBlock.Heading(level, $"Task comments and deprecated syntax ({report.Find<DeprecatedSyntaxFinding>(nameof(DeprecatedSyntaxScanner)).Count})");
        yield return new ReadableBlock.Paragraph(
            "A TODO/FIXME comment, a non-ANSI comparison operator, the \"= NULL\"/\"<> NULL\" silent always-false trap, a wildcard-free LIKE pattern, a legacy system compatibility view, a table hint without WITH, a numbered-procedure-group definition/invocation, a string-literal column alias, a removed legacy security stored procedure, or SET ROWCOUNT. The two NULL-comparison kinds are a real silent correctness trap under the default ANSI_NULLS ON setting; every other kind is a maintainability/forward-compatibility signal.");

        foreach (var group in report.Find<DeprecatedSyntaxFinding>(nameof(DeprecatedSyntaxScanner)).GroupBy(f => f.Kind).OrderBy(g => g.Key))
        {
            var ordered = group.ToList();
            yield return new ReadableBlock.Heading(level + 1, $"{HumanizeKindName(group.Key.ToString())} ({ordered.Count})");
            yield return new ReadableBlock.Paragraph(RuleDocSite.Url(SarifRuleCatalog.DeprecatedSyntaxRuleId(group.Key)));
            yield return new ReadableBlock.Table(
                [WhereHeader, DetailHeader],
                [.. ordered.Select(f => new List<string>
                {
                    Where(f.SourcePath, f.Line, dynamicSqlCallSite: null, pathBase, f.Confidence),
                    f.DetailText,
                })]);
        }
    }

    private static IEnumerable<ReadableBlock> StatementShape(ScanReport report, int level, string? pathBase)
    {
        if (report.Find<StatementShapeFinding>(nameof(StatementShapeScanner)).Count == 0)
        {
            yield break;
        }

        yield return new ReadableBlock.Heading(level, $"Statement-shape risks ({report.Find<StatementShapeFinding>(nameof(StatementShapeScanner)).Count})");
        yield return new ReadableBlock.Paragraph(
            "An INSERT with no explicit column list, an ordinal ORDER BY, a base table with no PRIMARY KEY, a routine missing SET NOCOUNT ON, or a bare SELECT *. The first two are correctness-adjacent (silently wrong the moment the target's/source's own column shape changes); the rest are maintainability/cost signals.");

        foreach (var group in report.Find<StatementShapeFinding>(nameof(StatementShapeScanner)).GroupBy(f => f.Kind).OrderBy(g => g.Key))
        {
            var ordered = group.ToList();
            yield return new ReadableBlock.Heading(level + 1, $"{HumanizeKindName(group.Key.ToString())} ({ordered.Count})");
            yield return new ReadableBlock.Paragraph(RuleDocSite.Url(SarifRuleCatalog.StatementShapeRuleId(group.Key)));
            yield return new ReadableBlock.Table(
                [WhereHeader, DetailHeader],
                [.. ordered.Select(f => new List<string>
                {
                    Where(f.SourcePath, f.Line, dynamicSqlCallSite: null, pathBase, f.Confidence),
                    f.DetailText,
                })]);
        }
    }

    private static IEnumerable<ReadableBlock> ControlFlowRisk(ScanReport report, int level, string? pathBase)
    {
        if (report.Find<ControlFlowRiskFinding>(nameof(ControlFlowRiskScanner)).Count == 0)
        {
            yield break;
        }

        yield return new ReadableBlock.Heading(level, $"Cursor and control-flow risks ({report.Find<ControlFlowRiskFinding>(nameof(ControlFlowRiskScanner)).Count})");
        yield return new ReadableBlock.Paragraph(
            "A cursor FETCH whose INTO list doesn't match its own cursor's defining SELECT column count (always fails at runtime, Msg 16924), an empty CATCH block (silently swallows every error), output emitted from a trigger (a SELECT or PRINT sent back to whatever connection fired the DML, not the calling application), a NOLOCK/READUNCOMMITTED dirty-read hint, the same expression passed twice to one call, a reference to @@IDENTITY (session-wide scope, prefer SCOPE_IDENTITY()), a GOTO statement, a simple CASE with no ELSE (silently evaluates to NULL when nothing matches), or a non-deterministic function (NEWID/RAND/CRYPT_GEN_RANDOM) used as a CASE input (oracle-confirmed to be re-evaluated separately per WHEN comparison, making every branch effectively unreachable).");

        foreach (var group in report.Find<ControlFlowRiskFinding>(nameof(ControlFlowRiskScanner)).GroupBy(f => f.Kind).OrderBy(g => g.Key))
        {
            var ordered = group.ToList();
            yield return new ReadableBlock.Heading(level + 1, $"{HumanizeKindName(group.Key.ToString())} ({ordered.Count})");
            yield return new ReadableBlock.Paragraph(RuleDocSite.Url(SarifRuleCatalog.ControlFlowRiskRuleId(group.Key)));
            yield return new ReadableBlock.Table(
                [WhereHeader, DetailHeader],
                [.. ordered.Select(f => new List<string>
                {
                    Where(f.SourcePath, f.Line, dynamicSqlCallSite: null, pathBase, f.Confidence),
                    f.DetailText,
                })]);
        }
    }

    private static IEnumerable<ReadableBlock> Security(ScanReport report, int level, string? pathBase)
    {
        if (report.Find<SecurityFinding>(nameof(SecurityScanner)).Count == 0)
        {
            yield break;
        }

        yield return new ReadableBlock.Heading(level, $"Security ({report.Find<SecurityFinding>(nameof(SecurityScanner)).Count})");
        yield return new ReadableBlock.Paragraph(
            "A credential-suggestive-named variable assigned a literal string, a hardcoded non-benign IP address, a HASHBYTES call naming a weak/deprecated algorithm (general use and, sharper, a security-sensitive context), and a dynamic SQL call site whose assembled text this tool cannot prove is free of runtime/external influence.");

        foreach (var group in report.Find<SecurityFinding>(nameof(SecurityScanner)).GroupBy(f => f.Kind).OrderBy(g => g.Key))
        {
            var ordered = group.ToList();
            yield return new ReadableBlock.Heading(level + 1, $"{HumanizeKindName(group.Key.ToString())} ({ordered.Count})");
            yield return new ReadableBlock.Paragraph(RuleDocSite.Url(SarifRuleCatalog.SecurityRuleId(group.Key)));
            yield return new ReadableBlock.Table(
                [WhereHeader, DetailHeader],
                [.. ordered.Select(f => new List<string>
                {
                    Where(f.SourcePath, f.Line, dynamicSqlCallSite: null, pathBase, f.Confidence),
                    f.DetailText,
                })]);
        }
    }

    private static IEnumerable<ReadableBlock> IndexDesign(ScanReport report, int level, string? pathBase)
    {
        if (report.Find<IndexDesignFinding>(nameof(IndexDesignScanner)).Count == 0)
        {
            yield break;
        }

        yield return new ReadableBlock.Heading(level, $"Physical/schema index design ({report.Find<IndexDesignFinding>(nameof(IndexDesignScanner)).Count})");
        yield return new ReadableBlock.Paragraph(
            "Live-mode only. A heap (no clustered index) carrying nonclustered indexes, and the sharper sibling, a heap whose own PRIMARY KEY is declared NONCLUSTERED - both pay an 8-byte RID lookup instead of a clustering-key seek. Clustering-key quality: a non-unique clustered index (hidden 4-byte uniquifier), and a uniqueidentifier clustered key defaulted to NEWID() (random insert order fragments the B-tree; NEWSEQUENTIALID() does not fire here). Also: duplicate/prefix-subsumed indexes, unindexed foreign keys, disabled/hypothetical indexes, a filtered index whose filter columns are absent from its own key/INCLUDE list, deprecated LOB column types (text/ntext/image, and timestamp vs. rowversion as a naming-only note), a float/real column used as an index key, and a statistics object marked NORECOMPUTE.");

        foreach (var group in report.Find<IndexDesignFinding>(nameof(IndexDesignScanner)).GroupBy(f => f.Kind).OrderBy(g => g.Key))
        {
            var ordered = group.ToList();
            yield return new ReadableBlock.Heading(level + 1, $"{HumanizeKindName(group.Key.ToString())} ({ordered.Count})");
            yield return new ReadableBlock.Paragraph(RuleDocSite.Url(SarifRuleCatalog.IndexDesignRuleId(group.Key)));
            yield return new ReadableBlock.Table(
                [WhereHeader, IndexHeader, DetailHeader],
                [.. ordered.Select(f => new List<string>
                {
                    Where(f.SourcePath, f.Line, dynamicSqlCallSite: null, pathBase, f.Confidence),
                    f.IndexName ?? (IsTableLevelIndexDesignKind(f.Kind) ? "(table-level)" : "<unnamed>"),
                    f.DetailText,
                })]);
        }
    }

    private static bool IsTableLevelIndexDesignKind(IndexDesignFindingKind kind) => kind switch
    {
        IndexDesignFindingKind.UnindexedForeignKey => true,
        _ => false,
    };

    private static IEnumerable<ReadableBlock> IdentityRange(ScanReport report, int level, string? pathBase)
    {
        if (report.Find<IdentityRangeFinding>(nameof(IdentityRangeScanner)).Count == 0)
        {
            yield break;
        }

        yield return new ReadableBlock.Heading(level, $"Identity/sequence range signals ({report.Find<IdentityRangeFinding>(nameof(IdentityRangeScanner)).Count})");
        yield return new ReadableBlock.Paragraph(
            "Live-mode only. An IDENTITY column that has consumed most of its declared type's representable range - data-state-decidable, meaningful ONLY against a production-shaped target; never read the absence of this finding as a passing signal on a low-value development database.");

        foreach (var group in report.Find<IdentityRangeFinding>(nameof(IdentityRangeScanner)).GroupBy(f => f.Kind).OrderBy(g => g.Key))
        {
            var ordered = group.ToList();
            yield return new ReadableBlock.Heading(level + 1, $"{HumanizeKindName(group.Key.ToString())} ({ordered.Count})");
            yield return new ReadableBlock.Paragraph(RuleDocSite.Url(SarifRuleCatalog.IdentityRangeRuleId(group.Key)));
            yield return new ReadableBlock.Table(
                [WhereHeader, ColumnHeader, DetailHeader],
                [.. ordered.Select(f => new List<string>
                {
                    Where(f.SourcePath, f.Line, dynamicSqlCallSite: null, pathBase, f.Confidence),
                    f.ColumnName,
                    f.DetailText,
                })]);
        }
    }

    private static IEnumerable<ReadableBlock> FloatEquality(ScanReport report, int level, string? pathBase)
    {
        if (report.Find<FloatEqualityFinding>(nameof(FloatEqualityPredicateScanner)).Count == 0)
        {
            yield break;
        }

        yield return new ReadableBlock.Heading(level, $"Float/real equality predicates ({report.Find<FloatEqualityFinding>(nameof(FloatEqualityPredicateScanner)).Count})");
        yield return new ReadableBlock.Paragraph(
            "A WHERE/ON equality predicate (=) compares a float/real (IEEE-754 approximate) column - a correctness risk, not a performance one: two values a person would call the same number can carry a different bit pattern and compare unequal, silently returning the wrong rows regardless of plan shape or indexing. Direct base-table columns in the immediate statement's own FROM clause only - a predicate reached through a view/CTE/derived table is not analyzed by this v1.");

        yield return new ReadableBlock.Paragraph(RuleDocSite.Url(SarifRuleCatalog.FloatEqualityRuleId));
        yield return new ReadableBlock.Table(
            [WhereHeader, ColumnHeader, "Type", DetailHeader],
            [.. report.Find<FloatEqualityFinding>(nameof(FloatEqualityPredicateScanner)).Select(f => new List<string>
            {
                Where(f.SourcePath, f.Line, dynamicSqlCallSite: null, pathBase, f.Confidence),
                $"{f.TableQualifiedName}.{f.ColumnName}",
                f.TypeDisplay,
                $"Compared with = at line {f.Line}, column {f.Column}.",
            })]);
    }

    private static IEnumerable<ReadableBlock> FloatOrderDependentAggregate(ScanReport report, int level, string? pathBase)
    {
        if (report.Find<FloatOrderDependentAggregateFinding>(nameof(FloatOrderDependentAggregateScanner)).Count == 0)
        {
            yield break;
        }

        yield return new ReadableBlock.Heading(level, $"Float/real columns in order-dependent aggregates ({report.Find<FloatOrderDependentAggregateFinding>(nameof(FloatOrderDependentAggregateScanner)).Count})");
        yield return new ReadableBlock.Paragraph(
            "SUM/AVG/VAR/VARP/STDEV/STDEVP is applied to a float/real (IEEE-754 approximate) column - these aggregates accumulate their running result in an order that depends on plan shape (serial vs parallel, degree of parallelism), so the identical aggregate over identical data can return a different bit pattern across runs, silently. MIN/MAX/COUNT are unaffected and not flagged.");

        yield return new ReadableBlock.Paragraph(RuleDocSite.Url(SarifRuleCatalog.FloatOrderDependentAggregateRuleId));
        yield return new ReadableBlock.Table(
            [WhereHeader, ColumnHeader, "Type", "Aggregate", DetailHeader],
            [.. report.Find<FloatOrderDependentAggregateFinding>(nameof(FloatOrderDependentAggregateScanner)).Select(f => new List<string>
            {
                Where(f.SourcePath, f.Line, dynamicSqlCallSite: null, pathBase, f.Confidence),
                $"{f.TableQualifiedName}.{f.ColumnName}",
                f.TypeDisplay,
                f.AggregateFunctionName,
                $"Aggregated at line {f.Line}, column {f.Column}.",
            })]);
    }

    private static IEnumerable<ReadableBlock> DynamicDataMasking(ScanReport report, int level, string? pathBase)
    {
        var findings = report.Find<DynamicDataMaskingFinding>(nameof(DynamicDataMaskingScanner));
        if (findings.Count == 0)
        {
            yield break;
        }

        yield return new ReadableBlock.Heading(level, $"Dynamic Data Masking silently defeated ({findings.Count})");
        yield return new ReadableBlock.Paragraph(
            "A masked column is used in a predicate/ordering/grouping context, where the engine evaluates against the real underlying value regardless of masking, or is used inside a computed SELECT-list expression, where the engine silently replaces the whole expression's result with the masking function's fixed sentinel instead of a real computed value - either way, masking's intended protection is defeated with no error and no visible sign at the call site.");

        yield return new ReadableBlock.Paragraph(RuleDocSite.Url(SarifRuleCatalog.DynamicDataMaskingRuleId(DynamicDataMaskingFindingKind.PredicateExposure)));
        yield return new ReadableBlock.Table(
            [WhereHeader, ColumnHeader, "Masking function", "Kind", "Context", DetailHeader],
            [.. findings.Select(f => new List<string>
            {
                Where(f.SourcePath, f.Line, dynamicSqlCallSite: null, pathBase, f.Confidence),
                $"{f.TableQualifiedName}.{f.ColumnName}",
                f.MaskingFunctionName,
                f.Kind == DynamicDataMaskingFindingKind.PredicateExposure ? "Real-value exposure" : "Sentinel collapse",
                f.ContextDescription,
                $"Referenced at line {f.Line}, column {f.Column}.",
            })]);
    }

    private static IEnumerable<ReadableBlock> AlwaysEncryptedOrderBy(ScanReport report, int level, string? pathBase)
    {
        if (report.Find<AlwaysEncryptedOrderByFinding>(nameof(AlwaysEncryptedOrderByScanner)).Count == 0)
        {
            yield break;
        }

        yield return new ReadableBlock.Heading(level, $"Always Encrypted ORDER BY ({report.Find<AlwaysEncryptedOrderByFinding>(nameof(AlwaysEncryptedOrderByScanner)).Count})");
        yield return new ReadableBlock.Paragraph(
            "An ORDER BY clause references an Always Encrypted column - the statement does not compile at all (Msg 33277), for both DETERMINISTIC and RANDOMIZED encryption types, regardless of whether the connecting client is itself Always-Encrypted-enabled. Direct base-table columns in the immediate statement's own top-level ORDER BY only - a window function's own OVER (... ORDER BY ...) and an encrypted column reached only through a view/CTE/derived table are not analyzed by this v1.");

        yield return new ReadableBlock.Paragraph(RuleDocSite.Url(SarifRuleCatalog.AlwaysEncryptedOrderByRuleId));
        yield return new ReadableBlock.Table(
            [WhereHeader, ColumnHeader, "Encryption type", DetailHeader],
            [.. report.Find<AlwaysEncryptedOrderByFinding>(nameof(AlwaysEncryptedOrderByScanner)).Select(f => new List<string>
            {
                Where(f.SourcePath, f.Line, dynamicSqlCallSite: null, pathBase, f.Confidence),
                $"{f.TableQualifiedName}.{f.ColumnName}",
                f.EncryptionTypeDisplay,
                $"Referenced in ORDER BY at line {f.Line}, column {f.Column}.",
            })]);
    }

    private static IEnumerable<ReadableBlock> AlwaysEncryptedKeyColumn(ScanReport report, int level, string? pathBase)
    {
        if (report.Find<AlwaysEncryptedKeyColumnFinding>(nameof(AlwaysEncryptedKeyColumnScanner)).Count == 0)
        {
            yield break;
        }

        yield return new ReadableBlock.Heading(level, $"Always Encrypted non-enclave key column ({report.Find<AlwaysEncryptedKeyColumnFinding>(nameof(AlwaysEncryptedKeyColumnScanner)).Count})");
        yield return new ReadableBlock.Paragraph(
            "A RANDOMIZED-encrypted column is used as a key column of an index, PRIMARY KEY/UNIQUE constraint, or statistics object, and the column encryption key backing it is tied to a column master key declared without ENCLAVE_COMPUTATIONS - the statement does not deploy (Msg 33573).");

        yield return new ReadableBlock.Paragraph(RuleDocSite.Url(SarifRuleCatalog.AlwaysEncryptedKeyColumnRuleId));
        yield return new ReadableBlock.Table(
            [WhereHeader, ColumnHeader, "Object", "Kind"],
            [.. report.Find<AlwaysEncryptedKeyColumnFinding>(nameof(AlwaysEncryptedKeyColumnScanner)).Select(f => new List<string>
            {
                Where(f.SourcePath, f.Line, dynamicSqlCallSite: null, pathBase, f.Confidence),
                $"{f.TableQualifiedName}.{f.ColumnName}",
                f.ObjectName,
                f.Kind switch
                {
                    AlwaysEncryptedKeyColumnKind.PrimaryKey => "PRIMARY KEY constraint",
                    AlwaysEncryptedKeyColumnKind.UniqueConstraint => "UNIQUE constraint",
                    AlwaysEncryptedKeyColumnKind.Statistics => "statistics",
                    _ => "index",
                },
            })]);
    }

    private static IEnumerable<ReadableBlock> AlterColumnSafety(ScanReport report, int level, string? pathBase)
    {
        if (report.Find<AlterColumnSafetyFinding>(nameof(AlterColumnSafetyScanner)).Count == 0)
        {
            yield break;
        }

        yield return new ReadableBlock.Heading(level, $"ALTER COLUMN safety ({report.Find<AlterColumnSafetyFinding>(nameof(AlterColumnSafetyScanner)).Count})");
        yield return new ReadableBlock.Paragraph(
            "An ALTER TABLE ... ALTER COLUMN either narrows a numeric or var-time column's declared precision/scale below its current catalog value, retypes a char/nchar/varchar/nvarchar column directly to binary/varbinary, or retypes a DATETIMEOFFSET column into an offset-unaware temporal type - all either fail or silently lose data at DDL time.");

        foreach (var group in report.Find<AlterColumnSafetyFinding>(nameof(AlterColumnSafetyScanner)).GroupBy(f => f.Kind).OrderBy(g => g.Key))
        {
            var ordered = group.ToList();
            yield return new ReadableBlock.Heading(level + 1, $"{AlterColumnSafetyTitle(group.Key)} ({ordered.Count})");
            yield return new ReadableBlock.Paragraph(RuleDocSite.Url(SarifRuleCatalog.AlterColumnSafetyRuleId(group.Key)));
            yield return new ReadableBlock.Table(
                [WhereHeader, ColumnHeader, "Previous type", "New type"],
                [.. ordered.Select(f => new List<string>
                {
                    Where(f.SourcePath, f.Line, dynamicSqlCallSite: null, pathBase, f.Confidence),
                    $"{f.TableQualifiedName}.{f.ColumnName}",
                    f.PreviousType.ToString(),
                    f.NewType.ToString(),
                })]);
        }
    }

    private static string AlterColumnSafetyTitle(AlterColumnSafetyKind kind) => kind switch
    {
        AlterColumnSafetyKind.PrecisionOrScaleNarrowing => "Precision/scale narrowing",
        AlterColumnSafetyKind.IncompatibleFamilyConversion => "Incompatible family conversion",
        AlterColumnSafetyKind.TemporalOffsetDropped => "Temporal offset dropped",
        _ => "Unknown",
    };

    private static IEnumerable<ReadableBlock> DropProtectedObject(ScanReport report, int level, string? pathBase)
    {
        if (report.Find<DropProtectedObjectFinding>(nameof(DropProtectedObjectScanner)).Count == 0)
        {
            yield break;
        }

        yield return new ReadableBlock.Heading(level, $"DROP against a protected object ({report.Find<DropProtectedObjectFinding>(nameof(DropProtectedObjectScanner)).Count})");
        yield return new ReadableBlock.Paragraph(
            "A DROP SCHEMA statement targets a schema this scan also saw own at least one object, or a DROP ROLE statement names one of the engine's fixed database roles - both always fail at deploy time.");

        foreach (var group in report.Find<DropProtectedObjectFinding>(nameof(DropProtectedObjectScanner)).GroupBy(f => f.Kind).OrderBy(g => g.Key))
        {
            var ordered = group.ToList();
            yield return new ReadableBlock.Heading(level + 1, $"{DropProtectedObjectTitle(group.Key)} ({ordered.Count})");
            yield return new ReadableBlock.Paragraph(RuleDocSite.Url(SarifRuleCatalog.DropProtectedObjectRuleId(group.Key)));
            yield return new ReadableBlock.Table(
                [WhereHeader, "Object"],
                [.. ordered.Select(f => new List<string>
                {
                    Where(f.SourcePath, f.Line, dynamicSqlCallSite: null, pathBase, f.Confidence),
                    f.ObjectName,
                })]);
        }
    }

    private static string DropProtectedObjectTitle(DropProtectedObjectKind kind) => kind switch
    {
        DropProtectedObjectKind.SchemaNotEmpty => "Schema not empty",
        DropProtectedObjectKind.FixedDatabaseRole => "Fixed database role",
        _ => "Unknown",
    };

    private static IEnumerable<ReadableBlock> OnlineRebuildLegacyLob(ScanReport report, int level, string? pathBase)
    {
        if (report.Find<OnlineRebuildLegacyLobFinding>(nameof(OnlineRebuildLegacyLobScanner)).Count == 0)
        {
            yield break;
        }

        yield return new ReadableBlock.Heading(level, $"Online index rebuild blocked by a legacy large-object column ({report.Find<OnlineRebuildLegacyLobFinding>(nameof(OnlineRebuildLegacyLobScanner)).Count})");
        yield return new ReadableBlock.Paragraph(
            "An ALTER TABLE ... REBUILD or ALTER INDEX ALL ... REBUILD statement specifies ONLINE = ON against a table carrying a TEXT/NTEXT/IMAGE column - the online rebuild always touches every column and never completes.");

        foreach (var group in report.Find<OnlineRebuildLegacyLobFinding>(nameof(OnlineRebuildLegacyLobScanner)).GroupBy(f => f.Kind).OrderBy(g => g.Key))
        {
            var ordered = group.ToList();
            yield return new ReadableBlock.Heading(level + 1, $"{OnlineRebuildLegacyLobTitle(group.Key)} ({ordered.Count})");
            yield return new ReadableBlock.Paragraph(RuleDocSite.Url(SarifRuleCatalog.OnlineRebuildLegacyLobRuleId(group.Key)));
            yield return new ReadableBlock.Table(
                [WhereHeader, "Table", "Column", "Type"],
                [.. ordered.Select(f => new List<string>
                {
                    Where(f.SourcePath, f.Line, dynamicSqlCallSite: null, pathBase, f.Confidence),
                    f.TableQualifiedName,
                    f.ColumnName,
                    f.TypeDisplay,
                })]);
        }
    }

    private static string OnlineRebuildLegacyLobTitle(OnlineRebuildLegacyLobKind kind) => kind switch
    {
        OnlineRebuildLegacyLobKind.AlterTableRebuild => "ALTER TABLE ... REBUILD",
        OnlineRebuildLegacyLobKind.AlterIndexAllRebuild => "ALTER INDEX ALL ... REBUILD",
        _ => "Unknown",
    };

    private static IEnumerable<ReadableBlock> OperandComparability(ScanReport report, int level, string? pathBase)
    {
        if (report.Find<OperandComparabilityFinding>(nameof(OperandComparabilityScanner)).Count == 0)
        {
            yield break;
        }

        yield return new ReadableBlock.Heading(level, $"Operand not comparable ({report.Find<OperandComparabilityFinding>(nameof(OperandComparabilityScanner)).Count})");
        yield return new ReadableBlock.Paragraph(
            "An xml, json, legacy large-object (text/ntext/image), or spatial (geometry/geography) column is referenced from a comparison, IN list, BETWEEN, NULLIF, ORDER BY, GROUP BY, SELECT DISTINCT, or a window function's PARTITION BY - these types are not comparable at all outside IS NULL (and, for the legacy large-object types, LIKE); the statement does not compile. Direct base-table columns resolved through the immediate statement's own FROM/CTE scope only - a column reached only through a view/derived table is not analyzed by this v1.");

        foreach (var group in report.Find<OperandComparabilityFinding>(nameof(OperandComparabilityScanner)).GroupBy(f => f.Kind).OrderBy(g => g.Key))
        {
            var ordered = group.ToList();
            yield return new ReadableBlock.Heading(level + 1, $"{HumanizeKindName(group.Key.ToString())} ({ordered.Count})");
            yield return new ReadableBlock.Paragraph(RuleDocSite.Url(SarifRuleCatalog.OperandComparabilityRuleId(group.Key)));
            yield return new ReadableBlock.Table(
                [WhereHeader, ColumnHeader, "Type", DetailHeader],
                [.. ordered.Select(f => new List<string>
                {
                    Where(f.SourcePath, f.Line, dynamicSqlCallSite: null, pathBase, f.Confidence),
                    $"{f.TableQualifiedName}.{f.ColumnName}",
                    f.TypeDisplay,
                    $"{f.Context}{(f.OperatorText is null ? "" : $" ({f.OperatorText})")} at line {f.Line}, column {f.Column}.",
                })]);
        }
    }

    private static IEnumerable<ReadableBlock> QueryAntiPattern(ScanReport report, int level, string? pathBase)
    {
        if (report.Find<QueryAntiPatternFinding>(nameof(QueryAntiPatternScanner)).Count == 0)
        {
            yield break;
        }

        yield return new ReadableBlock.Heading(level, $"Query anti-patterns ({report.Find<QueryAntiPatternFinding>(nameof(QueryAntiPatternScanner)).Count})");
        yield return new ReadableBlock.Paragraph(
            "Structurally-provable query shapes from two DBA-script-family sweep batches: a table variable used as a query source under a low compatibility level or a growing WHILE loop (stale/fixed cardinality estimate), a WHILE loop doing single-row DML keyed to its own tracked variable (RBAR), a cursor declared without LOCAL, COUNT(*) assigned to a variable then compared only to zero (a real full-set scan, unlike the inline scalar-subquery form the optimizer already rewrites), a non-aggregate HAVING predicate that belongs in WHERE, a UNION of provably disjoint branches, a SELECT DISTINCT join not backed by a unique index, an unqualified table reference at a real query site, three MERGE hazards (missing HOLDLOCK, a non-unique USING source, an unconditional DELETE branch), a recursive CTE with no MAXRECURSION option, a whole-table UPDATE/DELETE with no WHERE and no TOP, and a linked-server/cross-database table reference.");

        foreach (var group in report.Find<QueryAntiPatternFinding>(nameof(QueryAntiPatternScanner)).GroupBy(f => f.Kind).OrderBy(g => g.Key))
        {
            var ordered = group.ToList();
            yield return new ReadableBlock.Heading(level + 1, $"{HumanizeKindName(group.Key.ToString())} ({ordered.Count})");
            yield return new ReadableBlock.Paragraph(RuleDocSite.Url(SarifRuleCatalog.QueryAntiPatternRuleId(group.Key)));
            yield return new ReadableBlock.Table(
                [WhereHeader, DetailHeader],
                [.. ordered.Select(f => new List<string>
                {
                    Where(f.SourcePath, f.Line, dynamicSqlCallSite: null, pathBase, f.Confidence),
                    f.DetailText,
                })]);
        }
    }

    private static IEnumerable<ReadableBlock> IndexCoverage(ScanReport report, int level, string? pathBase)
    {
        if (report.Find<IndexCoverageFinding>(nameof(IndexCoverageScanner)).Count == 0)
        {
            yield break;
        }

        yield return new ReadableBlock.Heading(level, $"Index-coverage shapes ({report.Find<IndexCoverageFinding>(nameof(IndexCoverageScanner)).Count})");
        yield return new ReadableBlock.Paragraph(
            "A WHERE-equality seek against a base table's own single candidate nonclustered index (never fired when a real alternative index exists too) whose key + INCLUDE columns do not cover every other column the statement references on that table - oracle-confirmed via real plan XML that this shape produces a Key/RID Lookup (Lookup=\"1\") per matched row.");

        yield return new ReadableBlock.Paragraph(RuleDocSite.Url(SarifRuleCatalog.IndexCoverageRuleId(IndexCoverageFindingKind.KeyLookupProneIndex)));
        yield return new ReadableBlock.Table(
            [WhereHeader, TableHeader, IndexHeader, "Uncovered columns"],
            [.. report.Find<IndexCoverageFinding>(nameof(IndexCoverageScanner)).Select(f => new List<string>
            {
                Where(f.SourcePath, f.Line, dynamicSqlCallSite: null, pathBase, f.Confidence),
                f.TableQualifiedName,
                f.IndexName ?? "<unnamed>",
                string.Join(", ", f.UncoveredColumns),
            })]);
    }

    private static IEnumerable<ReadableBlock> TriggerCorrectness(ScanReport report, int level, string? pathBase)
    {
        if (report.Find<TriggerCorrectnessFinding>(nameof(TriggerCorrectnessScanner)).Count == 0)
        {
            yield break;
        }

        yield return new ReadableBlock.Heading(level, $"Trigger correctness ({report.Find<TriggerCorrectnessFinding>(nameof(TriggerCorrectnessScanner)).Count})");
        yield return new ReadableBlock.Paragraph(
            "A variable assigned from a single, unspecified row of inserted/deleted with no WHERE/TOP/aggregate - oracle-confirmed to silently bind an arbitrary row's value (and discard the rest) the moment the trigger's own DML affects more than one row - plus the sharper sub-kind where that value then drives a keyed UPDATE/DELETE straight-line in the same trigger body; a trigger with no IF NOT EXISTS/@@ROWCOUNT-style early-out guard (advisory, low confidence); and a trigger that writes directly back to its own target table, only reported when the connected database's own RECURSIVE_TRIGGERS option is live-confirmed on (oracle-confirmed the write genuinely re-fires the trigger rather than silently no-oping in that case).");

        foreach (var group in report.Find<TriggerCorrectnessFinding>(nameof(TriggerCorrectnessScanner)).GroupBy(f => f.Kind).OrderBy(g => g.Key))
        {
            var ordered = group.ToList();
            yield return new ReadableBlock.Heading(level + 1, $"{HumanizeKindName(group.Key.ToString())} ({ordered.Count})");
            yield return new ReadableBlock.Paragraph(RuleDocSite.Url(SarifRuleCatalog.TriggerCorrectnessRuleId(group.Key)));
            yield return new ReadableBlock.Table(
                [WhereHeader, "Trigger", DetailHeader],
                [.. ordered.Select(f => new List<string>
                {
                    Where(f.SourcePath, f.Line, dynamicSqlCallSite: null, pathBase, f.Confidence),
                    f.TriggerQualifiedName,
                    f.DetailText,
                })]);
        }
    }

    private static IEnumerable<ReadableBlock> CrossModuleLockOrder(ScanReport report, int level, string? pathBase)
    {
        if (report.Find<CrossModuleLockOrderFinding>(nameof(CrossModuleLockOrderScanner)).Count == 0)
        {
            yield break;
        }

        yield return new ReadableBlock.Heading(level, $"Cross-module lock ordering ({report.Find<CrossModuleLockOrderFinding>(nameof(CrossModuleLockOrderScanner)).Count})");
        yield return new ReadableBlock.Paragraph(
            "Two top-level procedures' own direct explicit-transaction write orders disagree on the relative lock order of the same two base tables - the textbook cross-session deadlock shape. V1 scope: direct DML targets only (never through a view or dynamic SQL), base tables only, writes inside an explicit BEGIN TRANSACTION only, and only top-level procedures' own direct bodies (not traced transitively through the call graph) - see the finding's own doc comment for the full precision story.");

        yield return new ReadableBlock.Paragraph(RuleDocSite.Url(SarifRuleCatalog.CrossModuleLockOrderRuleId));
        yield return new ReadableBlock.Table(
            ["First table", "Second table", "Writes first-then-second", "Writes second-then-first"],
            [.. report.Find<CrossModuleLockOrderFinding>(nameof(CrossModuleLockOrderScanner)).Select(f => new List<string>
            {
                f.FirstTableQualifiedName,
                f.SecondTableQualifiedName,
                $"{f.FirstTableFirstOrdering.ProcedureQualifiedName} ({Where(f.FirstTableFirstOrdering.SourcePath, f.FirstTableFirstOrdering.FirstWriteLine, dynamicSqlCallSite: null, pathBase, f.Confidence)})",
                $"{f.SecondTableFirstOrdering.ProcedureQualifiedName} ({Where(f.SecondTableFirstOrdering.SourcePath, f.SecondTableFirstOrdering.SecondWriteLine, dynamicSqlCallSite: null, pathBase, f.Confidence)})",
            })]);
    }

    private static IEnumerable<ReadableBlock> TriggerRecursionCycle(ScanReport report, int level, string? pathBase)
    {
        if (report.Find<TriggerRecursionCycleFinding>(nameof(TriggerRecursionCycleScanner)).Count == 0)
        {
            yield break;
        }

        yield return new ReadableBlock.Heading(level, $"Multi-hop trigger recursion cycles ({report.Find<TriggerRecursionCycleFinding>(nameof(TriggerRecursionCycleScanner)).Count})");
        yield return new ReadableBlock.Paragraph(
            "A directed cycle of triggers across two or more distinct tables (table A's trigger writes to table B, whose own trigger writes back toward A) - oracle-confirmed reachable while the server's own 'nested triggers' option is on (not RECURSIVE_TRIGGERS, which only governs a trigger recursing into itself), and confirmed to hit a real Msg 217 nesting-level-exceeded error once the cascade runs unbounded. V1 scope: only a direct INSERT/UPDATE/DELETE/MERGE target inside a trigger's own body counts as a hop, base tables only, cycle search capped at 8 hops - see the finding's own doc comment for the full precision story.");

        yield return new ReadableBlock.Paragraph(RuleDocSite.Url(SarifRuleCatalog.TriggerRecursionCycleRuleId));
        yield return new ReadableBlock.Table(
            [WhereHeader, "Cycle", "Hops"],
            [.. report.Find<TriggerRecursionCycleFinding>(nameof(TriggerRecursionCycleScanner)).Select(f => new List<string>
            {
                Where(f.Hops[0].SourcePath, f.Hops[0].TriggerLine, dynamicSqlCallSite: null, pathBase, f.Confidence),
                string.Join(" -> ", f.CycleTableQualifiedNames) + " -> " + f.CycleTableQualifiedNames[0],
                string.Join("; ", f.Hops.Select(h => $"{h.TriggerQualifiedName}: {h.FromTableQualifiedName} -> {h.ToTableQualifiedName} ({h.SourcePath}:{h.WriteLine})")),
            })]);
    }

    private static IEnumerable<ReadableBlock> NotInNullableSubquery(ScanReport report, int level, string? pathBase)
    {
        if (report.Find<NotInNullableSubqueryFinding>(nameof(NotInNullableSubqueryScanner)).Count == 0)
        {
            yield break;
        }

        yield return new ReadableBlock.Heading(level, $"NOT IN over a nullable subquery column ({report.Find<NotInNullableSubqueryFinding>(nameof(NotInNullableSubqueryScanner)).Count})");
        yield return new ReadableBlock.Paragraph(
            "'x NOT IN (SELECT y FROM t)' where y is a nullable column - a three-valued-logic correctness trap, not a plan-shape one. The instant the subquery produces one NULL row, the whole predicate evaluates to UNKNOWN for every outer row, so the query silently returns ZERO rows instead of the expected anti-join result - independent of any index or plan choice. Never fires when the subquery column is NOT NULL, or when the subquery already filters it with an unconditional 'WHERE y IS NOT NULL'.");

        yield return new ReadableBlock.Paragraph(RuleDocSite.Url(SarifRuleCatalog.NotInNullableSubqueryRuleId));
        yield return new ReadableBlock.Table(
            [WhereHeader, "Outer column", "Subquery column", IndexedHeader],
            [.. report.Find<NotInNullableSubqueryFinding>(nameof(NotInNullableSubqueryScanner)).Select(f => new List<string>
            {
                Where(f.SourcePath, f.Line, dynamicSqlCallSite: null, pathBase, f.Confidence),
                f.OuterColumnName ?? "<expression>",
                $"{f.SubqueryTableQualifiedName}.{f.SubqueryColumnName}",
                f.SubqueryColumnIndexed ? "yes" : "no",
            })]);
    }

    private static IEnumerable<ReadableBlock> NonUniqueUpdateSource(ScanReport report, int level, string? pathBase)
    {
        if (report.Find<NonUniqueUpdateSourceFinding>(nameof(NonUniqueUpdateSourceScanner)).Count == 0)
        {
            yield break;
        }

        yield return new ReadableBlock.Heading(level, $"UPDATE ... FROM without source uniqueness ({report.Find<NonUniqueUpdateSourceFinding>(nameof(NonUniqueUpdateSourceScanner)).Count})");
        yield return new ReadableBlock.Paragraph(
            "The joined source's own join columns carry no unique index/constraint - if a target row ever matches more than one source row, SQL Server silently picks a value from an unspecified one of them (plan-dependent, not guaranteed stable across executions). MERGE raises a hard error in this exact situation instead of picking silently. A structural defect, not a 'wrong for current data' one: no current duplicate has to exist for the statement to be unsafe, only the schema's own absence of a uniqueness guarantee. Never fires when the source's join columns are covered by a genuine unique index/constraint, or when the SET clause never reads from the non-unique source.");

        yield return new ReadableBlock.Paragraph(RuleDocSite.Url(SarifRuleCatalog.NonUniqueUpdateSourceRuleId));
        yield return new ReadableBlock.Table(
            [WhereHeader, "Target", "Source", "Join columns", "SET columns"],
            [.. report.Find<NonUniqueUpdateSourceFinding>(nameof(NonUniqueUpdateSourceScanner)).Select(f => new List<string>
            {
                Where(f.SourcePath, f.Line, dynamicSqlCallSite: null, pathBase, f.Confidence),
                f.TargetTableQualifiedName,
                f.SourceTableQualifiedName,
                string.Join(", ", f.JoinColumnNames),
                string.Join(", ", f.SetColumnNames),
            })]);
    }

    private static IEnumerable<ReadableBlock> CheckConstraintPredicateContradiction(ScanReport report, int level, string? pathBase)
    {
        if (report.Find<CheckConstraintPredicateContradictionFinding>(nameof(CheckConstraintPredicateContradictionScanner)).Count == 0)
        {
            yield break;
        }

        yield return new ReadableBlock.Heading(level, $"Predicates provably contradicting a trusted CHECK constraint or NOT NULL fact ({report.Find<CheckConstraintPredicateContradictionFinding>(nameof(CheckConstraintPredicateContradictionScanner)).Count})");
        yield return new ReadableBlock.Paragraph(
            "A WHERE predicate compares a column to a literal (or literal range) that is provably disjoint from a trusted, enabled CHECK constraint's own interval, or tests IS NULL against a column the catalog declares NOT NULL - oracle-confirmed the optimizer itself proves the branch unsatisfiable at compile time and folds it to a Constant Scan, so it can never return a row.");

        foreach (var group in report.Find<CheckConstraintPredicateContradictionFinding>(nameof(CheckConstraintPredicateContradictionScanner)).GroupBy(f => f.Kind).OrderBy(g => g.Key))
        {
            var ordered = group.ToList();
            var title = group.Key == CheckConstraintPredicateContradictionKind.CheckConstraintInterval ? "Contradicts a trusted CHECK constraint interval" : "IS NULL against a NOT NULL column";
            yield return new ReadableBlock.Heading(level + 1, $"{title} ({ordered.Count})");
            yield return new ReadableBlock.Paragraph(RuleDocSite.Url(SarifRuleCatalog.CheckConstraintPredicateContradictionRuleId(group.Key)));
            yield return new ReadableBlock.Table(
                [WhereHeader, TableHeader, ColumnHeader, ConstraintHeader],
                [.. ordered.Select(f => new List<string>
                {
                    Where(f.SourcePath, f.Line, dynamicSqlCallSite: null, pathBase, f.Confidence),
                    f.TableQualifiedName,
                    f.ColumnName,
                    f.ConstraintName ?? "-",
                })]);
        }
    }

    private static IEnumerable<ReadableBlock> ForcedSerial(ScanReport report, int level, string? pathBase)
    {
        if (report.Find<ForcedSerialFinding>(nameof(ForcedSerialScanner)).Count == 0)
        {
            yield break;
        }

        yield return new ReadableBlock.Heading(level, $"Forced-serial constructs ({report.Find<ForcedSerialFinding>(nameof(ForcedSerialScanner)).Count})");
        yield return new ReadableBlock.Paragraph(
            "Three independent, oracle-confirmed constructs that force SQL Server to disable parallelism (effective MAXDOP 1) for the statement/query that contains them - a performance-cost finding, not a correctness one, since the result never changes, only its cost. A table-variable modification's forced-serial scope is the one containing statement, not the whole batch/procedure. A FAST_FORWARD cursor (or the equivalent bare FORWARD_ONLY READ_ONLY) forces its own defining query serial - the opposite of the common 'always use LOCAL FAST_FORWARD' fetch-overhead advice, which remains correct advice for a different reason. STATIC/KEYSET/DYNAMIC cursors do not trigger this.");

        foreach (var group in report.Find<ForcedSerialFinding>(nameof(ForcedSerialScanner))
            .GroupBy(f => f.Kind)
            .OrderBy(g => g.Key))
        {
            var ordered = group.OrderBy(f => f.SourcePath, StringComparer.Ordinal).ThenBy(f => f.Line).ThenBy(f => f.Column).ToList();

            yield return new ReadableBlock.Heading(level + 1, $"{ForcedSerialTitle(group.Key)} ({ordered.Count})");
            yield return new ReadableBlock.Paragraph(RuleDocSite.Url(SarifRuleCatalog.ForcedSerialRuleId(group.Key)));
            yield return new ReadableBlock.Table(
                [WhereHeader, ModuleHeader, DetailHeader],
                [.. ordered.Select(f => new List<string>
                {
                    Where(f.SourcePath, f.Line, dynamicSqlCallSite: null, pathBase, f.Confidence),
                    f.ModuleQualifiedName,
                    f.DetailText ?? UnknownDisplay,
                })]);
        }
    }

    private static string ForcedSerialTitle(ForcedSerialFindingKind kind) => kind switch
    {
        ForcedSerialFindingKind.TableVariableModification => "Table variable modification",
        ForcedSerialFindingKind.FastForwardCursor => "FAST_FORWARD cursor",
        ForcedSerialFindingKind.NonParallelizableIntrinsic => "Non-parallelizable intrinsic",
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "Unhandled ForcedSerialFindingKind."),
    };

    private static IEnumerable<ReadableBlock> UntrustedConstraint(ScanReport report, int level, string? pathBase)
    {
        if (report.Find<UntrustedConstraintFinding>(nameof(UntrustedConstraintScanner)).Count == 0)
        {
            yield break;
        }

        yield return new ReadableBlock.Heading(level, $"Untrusted FK/CHECK constraints ({report.Find<UntrustedConstraintFinding>(nameof(UntrustedConstraintScanner)).Count})");
        yield return new ReadableBlock.Paragraph(
            "A constraint the engine itself does not trust - almost always the result of a WITH NOCHECK re-enabling ALTER TABLE statement (the default there, the opposite of the default on the original ADD CONSTRAINT). The optimizer forfeits join-elimination and other constraint-based rewrites for every query touching it, and the constraint may not actually hold over existing rows. A disabled constraint is not reported - it's openly off, not silently weaker than it looks.");

        foreach (var group in report.Find<UntrustedConstraintFinding>(nameof(UntrustedConstraintScanner)).GroupBy(f => f.Kind).OrderBy(g => g.Key))
        {
            var ordered = group.ToList();
            var title = group.Key == UntrustedConstraintFindingKind.ForeignKey ? "Foreign key" : "CHECK constraint";
            yield return new ReadableBlock.Heading(level + 1, $"{title} ({ordered.Count})");
            yield return new ReadableBlock.Paragraph(RuleDocSite.Url(SarifRuleCatalog.UntrustedConstraintRuleId(group.Key)));
            yield return new ReadableBlock.Table(
                [WhereHeader, ConstraintHeader, TableHeader],
                [.. ordered.Select(f => new List<string>
                {
                    Where(f.SourcePath, f.Line, dynamicSqlCallSite: null, pathBase, f.Confidence),
                    f.ConstraintName,
                    f.TableQualifiedName,
                })]);
        }
    }

    private static IEnumerable<ReadableBlock> CheckConstraint(ScanReport report, int level, string? pathBase)
    {
        if (report.Find<CheckConstraintFinding>(nameof(CheckConstraintScanner)).Count == 0)
        {
            yield break;
        }

        yield return new ReadableBlock.Heading(level, $"CHECK constraint text correctness ({report.Find<CheckConstraintFinding>(nameof(CheckConstraintScanner)).Count})");
        yield return new ReadableBlock.Paragraph(
            "A CHECK constraint whose own predicate text is wrong, independent of trust state. \"NULL not handled\": a nullable column's predicate has no IS NULL/IS NOT NULL test anywhere against it, so a NULL value silently passes under three-valued logic even though the constraint reads as if it forbids bad data. \"On IDENTITY column\": the predicate directly references an IDENTITY column - the counter advances through every failed insert, so a numeric-threshold CHECK here drifts as the counter climbs instead of staying fixed (a one-sided 'greater than' threshold fails deterministically until the counter catches up, then silently stops mattering forever; a one-sided 'less than' threshold does the opposite; other predicate shapes aren't guaranteed to settle either way).");

        foreach (var group in report.Find<CheckConstraintFinding>(nameof(CheckConstraintScanner)).GroupBy(f => f.Kind).OrderBy(g => g.Key))
        {
            var ordered = group.ToList();
            var title = group.Key == CheckConstraintFindingKind.NullNotHandled ? "NULL not handled" : "On IDENTITY column";
            yield return new ReadableBlock.Heading(level + 1, $"{title} ({ordered.Count})");
            yield return new ReadableBlock.Paragraph(RuleDocSite.Url(SarifRuleCatalog.CheckConstraintRuleId(group.Key)));
            yield return new ReadableBlock.Table(
                [WhereHeader, ConstraintHeader, TableHeader, ColumnHeader],
                [.. ordered.Select(f => new List<string>
                {
                    Where(f.SourcePath, f.Line, dynamicSqlCallSite: null, pathBase, f.Confidence),
                    f.ConstraintName,
                    f.TableQualifiedName,
                    f.ColumnName,
                })]);
        }
    }

    private static IEnumerable<ReadableBlock> DefaultNullableConstraint(ScanReport report, int level, string? pathBase)
    {
        if (report.Find<DefaultNullableConstraintFinding>(nameof(DefaultNullableConstraintScanner)).Count == 0)
        {
            yield break;
        }

        yield return new ReadableBlock.Heading(level, $"DEFAULT constraint on a still-nullable column ({report.Find<DefaultNullableConstraintFinding>(nameof(DefaultNullableConstraintScanner)).Count})");
        yield return new ReadableBlock.Paragraph(
            "A column carries a DEFAULT constraint but is still nullable. A DEFAULT only ever applies when the column is OMITTED from an INSERT's own column list; any caller that supplies NULL explicitly (a common ORM-generated full-column INSERT shape) bypasses the default entirely, silently, with no error.");

        yield return new ReadableBlock.Paragraph(RuleDocSite.Url(SarifRuleCatalog.DefaultNullableConstraintRuleId));
        yield return new ReadableBlock.Table(
            [WhereHeader, TableHeader, ColumnHeader, "Default"],
            [.. report.Find<DefaultNullableConstraintFinding>(nameof(DefaultNullableConstraintScanner)).Select(f => new List<string>
            {
                Where(f.SourcePath, f.Line, dynamicSqlCallSite: null, pathBase, f.Confidence),
                f.TableQualifiedName,
                f.ColumnName,
                f.DefaultDefinitionText,
            })]);
    }

    private static IEnumerable<ReadableBlock> TryCastComputedColumnPredicate(ScanReport report, int level, string? pathBase)
    {
        if (report.Find<TryCastComputedColumnPredicateFinding>(nameof(TryCastComputedColumnPredicateScanner)).Count == 0)
        {
            yield break;
        }

        yield return new ReadableBlock.Heading(level, $"TRY_CAST computed column referenced in a predicate ({report.Find<TryCastComputedColumnPredicateFinding>(nameof(TryCastComputedColumnPredicateScanner)).Count})");
        yield return new ReadableBlock.Paragraph(
            "A non-persisted computed column built on TRY_CAST is referenced inside a real filter-context predicate (WHERE/JOIN ON/HAVING) elsewhere in the corpus. TRY_CAST is session-DATEFORMAT-dependent and therefore classified non-deterministic by the engine, so this column can never be PERSISTED or indexed at all - the predicate can never seek through it no matter what index exists elsewhere on the table.");

        yield return new ReadableBlock.Paragraph(RuleDocSite.Url(SarifRuleCatalog.TryCastComputedColumnPredicateRuleId));
        yield return new ReadableBlock.Table(
            [WhereHeader, TableHeader, ColumnHeader, "Definition", "Definition site"],
            [.. report.Find<TryCastComputedColumnPredicateFinding>(nameof(TryCastComputedColumnPredicateScanner)).Select(f => new List<string>
            {
                Where(f.Location.SourcePath, f.Location.Line, dynamicSqlCallSite: null, pathBase, f.Confidence),
                f.TableQualifiedName,
                f.ColumnName,
                f.DefinitionText,
                Where(f.DefinitionLocation.SourcePath, f.DefinitionLocation.Line, dynamicSqlCallSite: null, pathBase, f.Confidence),
            })]);
    }

    private static IEnumerable<ReadableBlock> StaleSelectStarView(ScanReport report, int level, string? pathBase)
    {
        if (report.Find<StaleSelectStarViewFinding>(nameof(StaleSelectStarViewScanner)).Count == 0)
        {
            yield break;
        }

        yield return new ReadableBlock.Heading(level, $"SELECT * view stale against base table's current shape ({report.Find<StaleSelectStarViewFinding>(nameof(StaleSelectStarViewScanner)).Count})");
        yield return new ReadableBlock.Paragraph(
            "A view's own outermost SELECT * over a single base table has a compiled column list (frozen at CREATE/ALTER/sp_refreshview time) that no longer matches that base table's current column list - a later ALTER TABLE ADD/DROP COLUMN never propagates to the view. If a drop and a later add shifted column identity, the view may be silently surfacing real data under a stale, wrong column label, not merely missing/adding a column.");

        yield return new ReadableBlock.Paragraph(RuleDocSite.Url(SarifRuleCatalog.StaleSelectStarViewRuleId));
        yield return new ReadableBlock.Table(
            [WhereHeader, "View", "Base table", "View's columns", "Table's current columns"],
            [.. report.Find<StaleSelectStarViewFinding>(nameof(StaleSelectStarViewScanner)).Select(f => new List<string>
            {
                Where(f.SourcePath, f.Line, dynamicSqlCallSite: null, pathBase, f.Confidence),
                f.ViewQualifiedName,
                f.BaseTableQualifiedName,
                string.Join(", ", f.ViewCompiledColumns),
                string.Join(", ", f.BaseTableCurrentColumns),
            })]);
    }

    private static IEnumerable<ReadableBlock> BareTopNoOrderBy(ScanReport report, int level, string? pathBase)
    {
        if (report.Find<BareTopNoOrderByFinding>(nameof(BareTopNoOrderByScanner)).Count == 0)
        {
            yield break;
        }

        yield return new ReadableBlock.Heading(level, $"Bare TOP with no ORDER BY ({report.Find<BareTopNoOrderByFinding>(nameof(BareTopNoOrderByScanner)).Count})");
        yield return new ReadableBlock.Paragraph(
            "A TOP (n) with no ORDER BY anywhere in the same query. SQL Server's own documentation does not guarantee which rows TOP returns, or their order, without an explicit ORDER BY - the returned row set can change run to run with plan choice, parallelism, or statistics drift. TOP (100) PERCENT is excluded: 100 percent of a result set is every row regardless of TOP's own row-selection nondeterminism.");

        yield return new ReadableBlock.Paragraph(RuleDocSite.Url(SarifRuleCatalog.BareTopNoOrderByRuleId));
        yield return new ReadableBlock.Table(
            [WhereHeader],
            [.. report.Find<BareTopNoOrderByFinding>(nameof(BareTopNoOrderByScanner)).Select(f => new List<string>
            {
                Where(f.SourcePath, f.Line, dynamicSqlCallSite: null, pathBase, f.Confidence),
            })]);
    }

    private static IEnumerable<ReadableBlock> StringConcatNull(ScanReport report, int level, string? pathBase)
    {
        if (report.Find<StringConcatNullFinding>(nameof(StringConcatNullScanner)).Count == 0)
        {
            yield break;
        }

        yield return new ReadableBlock.Heading(level, $"+ concatenation of a nullable string column with no NULL guard ({report.Find<StringConcatNullFinding>(nameof(StringConcatNullScanner)).Count})");
        yield return new ReadableBlock.Paragraph(
            "A + concatenation chain includes a nullable string column with no ISNULL/COALESCE guard. Unlike CONCAT(), which treats a NULL operand as empty string, + propagates a single NULL operand to NULL for the whole expression, silently, with no error.");

        yield return new ReadableBlock.Paragraph(RuleDocSite.Url(SarifRuleCatalog.StringConcatNullRuleId));
        yield return new ReadableBlock.Table(
            [WhereHeader, TableHeader, ColumnHeader],
            [.. report.Find<StringConcatNullFinding>(nameof(StringConcatNullScanner)).Select(f => new List<string>
            {
                Where(f.SourcePath, f.Line, dynamicSqlCallSite: null, pathBase, f.Confidence),
                f.TableQualifiedName,
                f.ColumnName,
            })]);
    }

    private static IEnumerable<ReadableBlock> AggregateDivisionColumnstore(ScanReport report, int level, string? pathBase)
    {
        if (report.Find<AggregateDivisionColumnstoreFinding>(nameof(AggregateDivisionColumnstoreScanner)).Count == 0)
        {
            yield break;
        }

        yield return new ReadableBlock.Heading(level, $"CASE-guarded aggregate division on a columnstore-backed table ({report.Find<AggregateDivisionColumnstoreFinding>(nameof(AggregateDivisionColumnstoreScanner)).Count})");
        yield return new ReadableBlock.Paragraph(
            "An aggregate argument contains a CASE-guarded division by a non-constant divisor, on a table backed by a columnstore index. Historically reported as a class of bug where batch-mode (vectorized) execution does not reliably preserve the same per-row CASE-branch short-circuit elision rowstore scalar execution provides. Shipped as a structural risk flag only, Low confidence, after a genuine but unsuccessful attempt to reproduce a live failure against this tool's own standing engine build - not a proven-current-behavior claim.");

        yield return new ReadableBlock.Paragraph(RuleDocSite.Url(SarifRuleCatalog.AggregateDivisionColumnstoreRuleId));
        yield return new ReadableBlock.Table(
            [WhereHeader, "Aggregate", TableHeader],
            [.. report.Find<AggregateDivisionColumnstoreFinding>(nameof(AggregateDivisionColumnstoreScanner)).Select(f => new List<string>
            {
                Where(f.SourcePath, f.Line, dynamicSqlCallSite: null, pathBase, f.Confidence),
                f.AggregateFunctionName,
                f.TableQualifiedName,
            })]);
    }

    private static IEnumerable<ReadableBlock> SecurityPredicateIndex(ScanReport report, int level, string? pathBase)
    {
        if (report.Find<SecurityPredicateIndexFinding>(nameof(SecurityPredicateIndexScanner)).Count == 0)
        {
            yield break;
        }

        yield return new ReadableBlock.Heading(level, $"RLS predicate with no supporting index ({report.Find<SecurityPredicateIndexFinding>(nameof(SecurityPredicateIndexScanner)).Count})");
        yield return new ReadableBlock.Paragraph(
            "An enabled Row-Level Security FILTER predicate's own bound column(s) lead no active index on the secured table - the predicate is silently applied to every SELECT/UPDATE/DELETE against this table, so the engine cannot seek and must evaluate it as a residual, per-row filter over a full scan. Oracle-confirmed scan-vs-seek contrast; the checklist's own 'forces single-threaded execution' claim was not reproduced live on this tool's own standing engine build and is deliberately not asserted here.");

        yield return new ReadableBlock.Paragraph(RuleDocSite.Url(SarifRuleCatalog.SecurityPredicateIndexRuleId));
        yield return new ReadableBlock.Table(
            [WhereHeader, TableHeader, "Policy", "Predicate function", "Filtered column(s)"],
            [.. report.Find<SecurityPredicateIndexFinding>(nameof(SecurityPredicateIndexScanner)).Select(f => new List<string>
            {
                Where(f.SourcePath, f.Line, dynamicSqlCallSite: null, pathBase, f.Confidence),
                f.TableQualifiedName,
                f.PolicyQualifiedName,
                f.PredicateFunctionQualifiedName,
                string.Join(", ", f.FilteredColumns),
            })]);
    }

    private static IEnumerable<ReadableBlock> DanglingObjectReference(ScanReport report, int level, string? pathBase)
    {
        if (report.Find<DanglingObjectReferenceFinding>(DanglingObjectReferenceRuleId).Count == 0)
        {
            yield break;
        }

        yield return new ReadableBlock.Heading(level, $"Reference to a nonexistent object ({report.Find<DanglingObjectReferenceFinding>(DanglingObjectReferenceRuleId).Count})");
        yield return new ReadableBlock.Paragraph(
            "A stored procedure, view, function, or trigger names a table/view/synonym the engine's own binder cannot resolve to a real object right now - CREATE/ALTER succeeded anyway because SQL Server defers name resolution for a module body until it actually runs, so this looked completely clean until the first call that reaches it, which fails with Msg 208 (\"Invalid object name\").");
        yield return new ReadableBlock.Paragraph(RuleDocSite.Url(SarifRuleCatalog.DanglingObjectReferenceRuleId));

        yield return new ReadableBlock.Table(
            [WhereHeader, ModuleHeader, "Referenced object"],
            [.. report.Find<DanglingObjectReferenceFinding>(DanglingObjectReferenceRuleId).Select(f => new List<string>
            {
                Where(f.SourcePath, f.Line, dynamicSqlCallSite: null, pathBase, f.Confidence),
                $"{f.ModuleTypeDescription} {f.ModuleQualifiedName}",
                f.ReferencedSchemaName is { } schema ? $"{schema}.{f.ReferencedEntityName}" : f.ReferencedEntityName,
            })]);
    }

    private static IEnumerable<ReadableBlock> CascadingForeignKey(ScanReport report, int level, string? pathBase)
    {
        if (report.Find<CascadingForeignKeyFinding>(nameof(CascadingForeignKeyScanner)).Count == 0)
        {
            yield break;
        }

        yield return new ReadableBlock.Heading(level, $"Cascading FK actions ({report.Find<CascadingForeignKeyFinding>(nameof(CascadingForeignKeyScanner)).Count})");
        yield return new ReadableBlock.Paragraph(
            "A foreign key with a non-NO_ACTION ON DELETE/ON UPDATE action - a single DML statement against the referenced table silently touches every dependent row in the child table too, with no visible predicate change at the call site. Purely informational: this states the fact, not a proven cost - how many rows and how often depends on data this pass cannot see.");

        yield return new ReadableBlock.Paragraph(RuleDocSite.Url(SarifRuleCatalog.CascadingForeignKeyRuleId));
        yield return new ReadableBlock.Table(
            [WhereHeader, ConstraintHeader, "Parent", "Referenced", "Delete action", "Update action"],
            [.. report.Find<CascadingForeignKeyFinding>(nameof(CascadingForeignKeyScanner)).Select(f => new List<string>
            {
                Where(f.SourcePath, f.Line, dynamicSqlCallSite: null, pathBase, f.Confidence),
                f.ConstraintName,
                f.ParentTableQualifiedName,
                f.ReferencedTableQualifiedName,
                f.DeleteAction.ToString(),
                f.UpdateAction.ToString(),
            })]);
    }

    private static IEnumerable<ReadableBlock> MultiReferencedCte(ScanReport report, int level, string? pathBase)
    {
        if (report.Find<MultiReferencedCteFinding>(nameof(MultiReferencedCteScanner)).Count == 0)
        {
            yield break;
        }

        yield return new ReadableBlock.Heading(level, $"Multi-referenced CTEs ({report.Find<MultiReferencedCteFinding>(nameof(MultiReferencedCteScanner)).Count})");
        yield return new ReadableBlock.Paragraph(
            "SQL Server does not materialize a plain CTE once and reuse it - each reference downstream of the WITH clause independently re-runs the CTE's own defining query, confirmed directly against the oracle (a base table's own scan count doubled under a CTE referenced twice). A self-reference inside a recursive CTE's own body is never counted - that's the structurally mandated recursion mechanism, not optional re-invocation.");

        yield return new ReadableBlock.Paragraph(RuleDocSite.Url(SarifRuleCatalog.MultiReferencedCteRuleId));
        yield return new ReadableBlock.Table(
            [WhereHeader, "CTE", "References"],
            [.. report.Find<MultiReferencedCteFinding>(nameof(MultiReferencedCteScanner)).Select(f => new List<string>
            {
                Where(f.SourcePath, f.Line, dynamicSqlCallSite: null, pathBase, f.Confidence),
                f.CteName,
                f.ReferenceCount.ToString(CultureInfo.InvariantCulture),
            })]);
    }

    private static IEnumerable<ReadableBlock> RecursiveCteAnchorTypeMismatch(ScanReport report, int level, string? pathBase)
    {
        if (report.Find<RecursiveCteAnchorTypeMismatchFinding>(nameof(RecursiveCteAnchorTypeMismatchScanner)).Count == 0)
        {
            yield break;
        }

        yield return new ReadableBlock.Heading(level, $"Recursive CTE anchor/recursive member type mismatches ({report.Find<RecursiveCteAnchorTypeMismatchFinding>(nameof(RecursiveCteAnchorTypeMismatchScanner)).Count})");
        yield return new ReadableBlock.Paragraph(
            "T-SQL requires a recursive CTE's recursive member to resolve each column to exactly the anchor member's own type - oracle-confirmed as a hard compile-time error (Msg 240, \"Types don't match between the anchor and the recursive part\") that blocks even CREATE PROCEDURE/CREATE VIEW from succeeding.");

        yield return new ReadableBlock.Paragraph(RuleDocSite.Url(SarifRuleCatalog.RecursiveCteAnchorTypeMismatchRuleId));
        yield return new ReadableBlock.Table(
            [WhereHeader, "CTE", "Column", "Anchor type", "Recursive member type"],
            [.. report.Find<RecursiveCteAnchorTypeMismatchFinding>(nameof(RecursiveCteAnchorTypeMismatchScanner)).Select(f => new List<string>
            {
                Where(f.SourcePath, f.Line, dynamicSqlCallSite: null, pathBase, f.Confidence),
                f.CteName,
                f.ColumnName,
                f.AnchorTypeDisplay,
                f.RecursiveTypeDisplay,
            })]);
    }

    private static IEnumerable<ReadableBlock> NestedViewDepth(ScanReport report, int level, string? pathBase)
    {
        if (report.Find<NestedViewDepthFinding>(nameof(NestedViewDepthScanner)).Count == 0)
        {
            yield break;
        }

        yield return new ReadableBlock.Heading(level, $"Nested-view depth ({report.Find<NestedViewDepthFinding>(nameof(NestedViewDepthScanner)).Count})");
        yield return new ReadableBlock.Paragraph(
            $"A view/inline TVF nested {NestedViewDepthScanner.DepthThreshold}+ view/TVF layers deep before reaching a base table - structural depth, not a claim the query is currently slow. A change to a base table now has to be traced through multiple independent view layers before its blast radius is understood, and each layer is a place a SELECT */column-list mismatch or silent type widening can hide. Catalog/lineage-only, reported once per view regardless of whether any scanned query calls it.");

        yield return new ReadableBlock.Paragraph(RuleDocSite.Url(SarifRuleCatalog.NestedViewDepthRuleId));
        yield return new ReadableBlock.Table(
            [WhereHeader, "View", "Depth", "Chain", "Base tables"],
            [.. report.Find<NestedViewDepthFinding>(nameof(NestedViewDepthScanner)).Select(f => new List<string>
            {
                Where(f.SourcePath, f.Line, dynamicSqlCallSite: null, pathBase, f.Confidence),
                f.ViewQualifiedName,
                f.Depth.ToString(CultureInfo.InvariantCulture),
                string.Join(" -> ", f.Chain),
                string.Join(", ", f.BaseTables),
            })]);
    }

    private static IEnumerable<ReadableBlock> PostExpansionJoinWidth(ScanReport report, int level, string? pathBase)
    {
        if (report.Find<PostExpansionJoinWidthFinding>(nameof(PostExpansionJoinWidthScanner)).Count == 0)
        {
            yield break;
        }

        yield return new ReadableBlock.Heading(level, $"Post-expansion join width ({report.Find<PostExpansionJoinWidthFinding>(nameof(PostExpansionJoinWidthScanner)).Count})");
        yield return new ReadableBlock.Paragraph(
            "The written FROM/JOIN table count is meaningless when half the sources are views - the number that matters is the EXPANDED one, base tables after resolving every view/inline-TVF reference transitively. Ranked by the gap between written and expanded count. Deliberately makes no claim about a specific 'past N the optimizer gives up exhaustive search' threshold - that number is unconfirmed folklore, not yet oracle-verified on this engine.");

        yield return new ReadableBlock.Paragraph(RuleDocSite.Url(SarifRuleCatalog.PostExpansionJoinWidthRuleId));
        yield return new ReadableBlock.Table(
            [WhereHeader, "Written", "Expanded", "Inflating source(s)", "Unexpanded?"],
            [.. report.Find<PostExpansionJoinWidthFinding>(nameof(PostExpansionJoinWidthScanner)).Select(f => new List<string>
            {
                Where(f.SourcePath, f.Line, dynamicSqlCallSite: null, pathBase, f.Confidence),
                f.WrittenCount.ToString(CultureInfo.InvariantCulture),
                f.ExpandedCount.ToString(CultureInfo.InvariantCulture),
                string.Join(", ", f.InflatingSources),
                f.PartiallyUnexpanded ? "yes" : "no",
            })]);
    }

    private static IEnumerable<ReadableBlock> SelectStarView(ScanReport report, int level, string? pathBase)
    {
        if (report.Find<SelectStarViewFinding>(nameof(SelectStarViewScanner)).Count == 0)
        {
            yield break;
        }

        yield return new ReadableBlock.Heading(level, $"SELECT * inside a nested view/TVF ({report.Find<SelectStarViewFinding>(nameof(SelectStarViewScanner)).Count})");
        yield return new ReadableBlock.Paragraph(
            "A view/inline TVF nested 1+ view/TVF layers deep whose own outermost SELECT is a bare or qualified * - its column list is frozen at CREATE/ALTER time and silently disagrees with the base table after any change, confirmed to survive even a live describe-only probe and real execution until sp_refreshview runs. Only listed here where a real consuming query explicitly selects a strict, named subset of the view's full column set - a consumer doing SELECT * from the view never narrows anything and is never matched.");

        yield return new ReadableBlock.Paragraph(RuleDocSite.Url(SarifRuleCatalog.SelectStarViewRuleId));
        yield return new ReadableBlock.Table(
            [WhereHeader, "View", "View columns", "Consumer selects"],
            [.. report.Find<SelectStarViewFinding>(nameof(SelectStarViewScanner)).Select(f => new List<string>
            {
                Where(f.ConsumerSourcePath, f.ConsumerLine, dynamicSqlCallSite: null, pathBase, f.Confidence),
                f.ViewQualifiedName,
                $"{f.ViewFullColumns.Count} ({string.Join(", ", f.ViewFullColumns)})",
                string.Join(", ", f.ConsumerSelectedColumns),
            })]);
    }

    private static IEnumerable<ReadableBlock> UnparameterizedDynamicSql(ScanReport report, int level, string? pathBase)
    {
        if (report.Find<UnparameterizedDynamicSqlFinding>(DynamicSqlRuleId).Count == 0)
        {
            yield break;
        }

        yield return new ReadableBlock.Heading(level, $"Concatenated values in dynamic SQL ({report.Find<UnparameterizedDynamicSqlFinding>(DynamicSqlRuleId).Count})");
        yield return new ReadableBlock.Paragraph(
            "A value this scanner proved constant (CLAUDE.md's Tier A dynamic-SQL folding) was spliced into an EXEC/sp_executesql call's own SQL text via string concatenation, rather than authored as one fixed literal or passed through sp_executesql's own @params. Every distinct concatenated value compiles its own cached plan - real plan-cache pollution, oracle-confirmed. The 'EXEC(string), sp_executesql available' kind fires only on a genuine EXEC(string)/EXEC(@sql) call site and names the specific fix: switch to sp_executesql and pass the value as a real parameter.");

        foreach (var group in report.Find<UnparameterizedDynamicSqlFinding>(DynamicSqlRuleId).GroupBy(f => f.Kind).OrderBy(g => g.Key))
        {
            var ordered = group.ToList();
            var title = group.Key == UnparameterizedDynamicSqlFindingKind.ExecStringConcatenatesParameterizableValue
                ? "EXEC(string), sp_executesql available"
                : "Concatenated value in constant SQL";
            yield return new ReadableBlock.Heading(level + 1, $"{title} ({ordered.Count})");
            yield return new ReadableBlock.Paragraph(RuleDocSite.Url(SarifRuleCatalog.UnparameterizedDynamicSqlRuleId(group.Key)));
            yield return new ReadableBlock.Table(
                [WhereHeader],
                [.. ordered.Select(f => new List<string>
                {
                    Where(f.SourcePath, f.Line, dynamicSqlCallSite: null, pathBase, f.Confidence),
                })]);
        }
    }

    private static IEnumerable<ReadableBlock> TempTableExecShape(ScanReport report, int level, string? pathBase)
    {
        if (report.Find<TempTableExecShapeFinding>(TempTableExecShapeRuleId).Count == 0)
        {
            yield break;
        }

        yield return new ReadableBlock.Heading(level, $"INSERT INTO #temp EXEC proc shape mismatches ({report.Find<TempTableExecShapeFinding>(TempTableExecShapeRuleId).Count})");
        yield return new ReadableBlock.Paragraph(
            "INSERT INTO #temp EXEC OtherProc binds the executed proc's result set to #temp's own declared columns purely by POSITION, live-verified against the executed proc's real, engine-described shape (sys.dm_exec_describe_first_result_set, compile-only). A column-count mismatch raises a hard runtime error (Msg 213/8164) every time the statement runs. A column-type mismatch at a matching position risks the same class of silent data loss WriteLossFinding already reports for INSERT/UPDATE assignments - live-mode only, since the verdict depends on a real database round trip.");

        foreach (var group in report.Find<TempTableExecShapeFinding>(TempTableExecShapeRuleId).GroupBy(f => f.Kind).OrderBy(g => g.Key))
        {
            var ordered = group.ToList();
            var title = group.Key == TempTableExecShapeFindingKind.ColumnCountMismatch ? "Column count mismatch" : "Column type mismatch";
            yield return new ReadableBlock.Heading(level + 1, $"{title} ({ordered.Count})");
            yield return new ReadableBlock.Paragraph(RuleDocSite.Url(SarifRuleCatalog.TempTableExecShapeRuleId(group.Key)));
            yield return new ReadableBlock.Table(
                [WhereHeader, DetailHeader],
                [.. ordered.Select(f => new List<string>
                {
                    Where(f.SourcePath, f.Line, dynamicSqlCallSite: null, pathBase, f.Confidence),
                    f.Kind == TempTableExecShapeFindingKind.ColumnCountMismatch
                        ? $"{f.TempTableQualifiedName} INSERT targets {f.TempTableDeclaredColumnCount} column(s); {f.ExecutedProcQualifiedName} describes {f.DescribedColumnCount}"
                        : $"{f.TempTableQualifiedName} position {f.ColumnPosition} ('{f.ColumnName}', {f.TempColumnTypeDisplay}) <- {f.ExecutedProcQualifiedName} ({f.DescribedColumnTypeDisplay}): {f.WriteLoss}",
                })]);
        }
    }

    private static IEnumerable<ReadableBlock> ExecResultSetsShape(ScanReport report, int level, string? pathBase)
    {
        if (report.Find<ExecResultSetsShapeFinding>(ExecResultSetsShapeRuleId).Count == 0)
        {
            yield break;
        }

        yield return new ReadableBlock.Heading(level, $"EXEC ... WITH RESULT SETS shape mismatches ({report.Find<ExecResultSetsShapeFinding>(ExecResultSetsShapeRuleId).Count})");
        yield return new ReadableBlock.Paragraph(
            "EXEC OtherProc WITH RESULT SETS ((...)) binds the executed proc's result set to the clause's own declared columns purely by POSITION, live-verified against the executed proc's real, engine-described shape (sys.dm_exec_describe_first_result_set, compile-only). A column-count mismatch raises a hard runtime error (Msg 11537) every time the statement runs. A column-type mismatch at a matching position risks the same class of silent data loss WriteLossFinding already reports for INSERT/UPDATE assignments - live-mode only, since the verdict depends on a real database round trip.");

        foreach (var group in report.Find<ExecResultSetsShapeFinding>(ExecResultSetsShapeRuleId).GroupBy(f => f.Kind).OrderBy(g => g.Key))
        {
            var ordered = group.ToList();
            var title = group.Key == ExecResultSetsShapeFindingKind.ColumnCountMismatch ? "Column count mismatch" : "Column type mismatch";
            yield return new ReadableBlock.Heading(level + 1, $"{title} ({ordered.Count})");
            yield return new ReadableBlock.Paragraph(RuleDocSite.Url(SarifRuleCatalog.ExecResultSetsShapeRuleId(group.Key)));
            yield return new ReadableBlock.Table(
                [WhereHeader, DetailHeader],
                [.. ordered.Select(f => new List<string>
                {
                    Where(f.SourcePath, f.Line, dynamicSqlCallSite: null, pathBase, f.Confidence),
                    f.Kind == ExecResultSetsShapeFindingKind.ColumnCountMismatch
                        ? $"{f.ExecutedProcQualifiedName} WITH RESULT SETS declares {f.DeclaredColumnCount} column(s); describes {f.DescribedColumnCount}"
                        : $"{f.ExecutedProcQualifiedName} WITH RESULT SETS position {f.ColumnPosition} ('{f.ColumnName}', {f.DeclaredColumnTypeDisplay}) <- described ({f.DescribedColumnTypeDisplay}): {f.WriteLoss}",
                })]);
        }
    }

    private static IEnumerable<ReadableBlock> SelfReferencingDml(ScanReport report, int level, string? pathBase)
    {
        if (report.Find<SelfReferencingDmlFinding>(nameof(SelfReferencingDmlScanner)).Count == 0)
        {
            yield break;
        }

        yield return new ReadableBlock.Heading(level, $"Self-referencing DML - Halloween Protection risk ({report.Find<SelfReferencingDmlFinding>(nameof(SelfReferencingDmlScanner)).Count})");
        yield return new ReadableBlock.Paragraph(
            "An INSERT/UPDATE/DELETE/MERGE whose own read side (a self-join, a WHERE/SET subquery, or a view over the same base table) also names the exact table it writes to. Oracle-confirmed to force extra defensive plan work an otherwise-identical statement reading a different table never pays - a LogicalOp=\"Eager Spool\" for INSERT/DELETE, an extra Sort operator for UPDATE ... FROM self-joins and MERGE (no spool at all in that case). A performance-cost finding, not a correctness one - the result is identical either way.");

        yield return new ReadableBlock.Paragraph(RuleDocSite.Url(SarifRuleCatalog.SelfReferencingDmlRuleId));
        yield return new ReadableBlock.Table(
            [WhereHeader, "Statement", "Target", DetailHeader],
            [.. report.Find<SelfReferencingDmlFinding>(nameof(SelfReferencingDmlScanner)).Select(f => new List<string>
            {
                Where(f.SourcePath, f.Line, dynamicSqlCallSite: null, pathBase, f.Confidence),
                f.StatementKind,
                f.TargetTableQualifiedName,
                f.Kind == SelfReferencingDmlFindingKind.ThroughView
                    ? $"read side reaches the target through view '{f.ReadSideQualifiedName}'"
                    : "read side names the target table directly",
            })]);
    }

    private static IEnumerable<ReadableBlock> TemporalTableHistoryIndexGap(ScanReport report, int level, string? pathBase)
    {
        if (report.Find<TemporalTableHistoryIndexGapFinding>(nameof(TemporalTableHistoryIndexGapScanner)).Count == 0)
        {
            yield break;
        }

        yield return new ReadableBlock.Heading(level, $"Temporal table history-side index gaps ({report.Find<TemporalTableHistoryIndexGapFinding>(nameof(TemporalTableHistoryIndexGapScanner)).Count})");
        yield return new ReadableBlock.Paragraph(
            "A system-versioned temporal table's CURRENT side carries a nonclustered index with no structurally matching index (same key columns, same order) on its HISTORY side. FOR SYSTEM_TIME AS OF/BETWEEN rewrites to a UNION ALL of the two tables - oracle-confirmed directly (real seeded data, UPDATE STATISTICS ... WITH FULLSCAN on both sides): a predicate that seeks the current-table branch via this index degrades to a full Clustered Index Scan of the whole history table when the gap exists, and seeks both branches once a matching index is added. PRIMARY KEY/UNIQUE-constraint indexes on the current side are never compared - the engine itself refuses either constraint on a temporal history table (Msg 13558/13583), so flagging them would be a guaranteed-always-fire signal with no possible fix.");

        yield return new ReadableBlock.Paragraph(RuleDocSite.Url(SarifRuleCatalog.TemporalTableHistoryIndexGapRuleId));
        yield return new ReadableBlock.Table(
            [WhereHeader, "Current table", "History table", IndexHeader, "Key columns"],
            [.. report.Find<TemporalTableHistoryIndexGapFinding>(nameof(TemporalTableHistoryIndexGapScanner)).Select(f => new List<string>
            {
                Where(f.SourcePath, f.Line, dynamicSqlCallSite: null, pathBase, f.Confidence),
                f.CurrentTableQualifiedName,
                f.HistoryTableQualifiedName,
                f.CurrentIndexName ?? "(unnamed)",
                string.Join(", ", f.KeyColumns),
            })]);
    }

    private static IEnumerable<ReadableBlock> GeneratedAlwaysColumnAssignment(ScanReport report, int level, string? pathBase)
    {
        if (report.Find<GeneratedAlwaysColumnAssignmentFinding>(nameof(GeneratedAlwaysColumnAssignmentScanner)).Count == 0)
        {
            yield break;
        }

        yield return new ReadableBlock.Heading(level, $"Explicit assignments to a GENERATED ALWAYS temporal period column ({report.Find<GeneratedAlwaysColumnAssignmentFinding>(nameof(GeneratedAlwaysColumnAssignmentScanner)).Count})");
        yield return new ReadableBlock.Paragraph(
            "An INSERT/UPDATE/MERGE names a system-versioned temporal table's GENERATED ALWAYS AS ROW START/END period column - oracle-confirmed a hard compile/runtime error unconditionally (Msg 13536 for an explicit INSERT value, Msg 13537 for any UPDATE assignment, DEFAULT included).");

        foreach (var group in report.Find<GeneratedAlwaysColumnAssignmentFinding>(nameof(GeneratedAlwaysColumnAssignmentScanner)).GroupBy(f => f.Kind).OrderBy(g => g.Key))
        {
            var ordered = group.ToList();
            var title = group.Key == GeneratedAlwaysColumnAssignmentKind.ExplicitInsertValue
                ? "Explicit INSERT value (Msg 13536)"
                : "UPDATE SET assignment (Msg 13537)";
            yield return new ReadableBlock.Heading(level + 1, $"{title} ({ordered.Count})");
            yield return new ReadableBlock.Paragraph(RuleDocSite.Url(SarifRuleCatalog.GeneratedAlwaysColumnAssignmentRuleId(group.Key)));
            yield return new ReadableBlock.Table(
                [WhereHeader, TableHeader, ColumnHeader],
                [.. ordered.Select(f => new List<string>
                {
                    Where(f.SourcePath, f.Line, dynamicSqlCallSite: null, pathBase, f.Confidence),
                    f.TableQualifiedName,
                    f.ColumnName,
                })]);
        }
    }

    private static IEnumerable<ReadableBlock> ModuleCompileFlag(ScanReport report, int level, string? pathBase)
    {
        if (report.Find<ModuleCompileFlagFinding>(nameof(ModuleCompileFlagScanner)).Count == 0)
        {
            yield break;
        }

        yield return new ReadableBlock.Heading(level, $"Module compile flags ({report.Find<ModuleCompileFlagFinding>(nameof(ModuleCompileFlagScanner)).Count})");
        yield return new ReadableBlock.Paragraph(
            "Two independent sys.sql_modules catalog flags, each baked in wholesale at CREATE/ALTER time: WITH RECOMPILE (every call compiles a fresh plan and discards it, invisible to any plan-cache-based monitoring), and a non-schema-bound table-valued function's own RETURNS TABLE declaring a character column with no explicit COLLATE (its collation was resolved against the database's default at CREATE/ALTER time and silently disagrees with the database's collation after any later ALTER DATABASE ... COLLATE). Schema-bound modules are deliberately excluded from the second kind - oracle-confirmed that schema-binding sets the underlying flag unconditionally, string data or not, so it carries no differentiating signal there.");

        foreach (var group in report.Find<ModuleCompileFlagFinding>(nameof(ModuleCompileFlagScanner)).GroupBy(f => f.Kind).OrderBy(g => g.Key))
        {
            var ordered = group.ToList();
            var title = group.Key == ModuleCompileFlagFindingKind.RecompilesEveryCall
                ? "WITH RECOMPILE"
                : "RETURNS TABLE column uses database collation";
            yield return new ReadableBlock.Heading(level + 1, $"{title} ({ordered.Count})");
            yield return new ReadableBlock.Paragraph(RuleDocSite.Url(SarifRuleCatalog.ModuleCompileFlagRuleId(group.Key)));
            yield return new ReadableBlock.Table(
                [WhereHeader, ModuleHeader],
                [.. ordered.Select(f => new List<string>
                {
                    Where(f.SourcePath, f.Line, dynamicSqlCallSite: null, pathBase, f.Confidence),
                    f.ModuleQualifiedName,
                })]);
        }
    }

    private static IEnumerable<ReadableBlock> WindowFrame(ScanReport report, int level, string? pathBase)
    {
        if (report.Find<WindowFrameFinding>(nameof(WindowFrameScanner)).Count == 0)
        {
            yield break;
        }

        yield return new ReadableBlock.Heading(level, $"RANGE window-function frames ({report.Find<WindowFrameFinding>(nameof(WindowFrameScanner)).Count})");
        yield return new ReadableBlock.Paragraph(
            "A window function's OVER clause uses (explicitly, or by T-SQL's own silent default when ORDER BY is present with no frame clause at all) a RANGE frame rather than ROWS - oracle-measured to cost materially more CPU at the Window Spool operator than the equivalent ROWS frame, though both compile to the identical Window Spool physical operator, not an on-disk-vs-not distinction.");

        foreach (var group in report.Find<WindowFrameFinding>(nameof(WindowFrameScanner)).GroupBy(f => f.Kind).OrderBy(g => g.Key))
        {
            var ordered = group.ToList();
            var title = group.Key == WindowFrameFindingKind.ExplicitRangeFrame ? "Explicit RANGE" : "Implicit default (RANGE)";
            yield return new ReadableBlock.Heading(level + 1, $"{title} ({ordered.Count})");
            yield return new ReadableBlock.Paragraph(RuleDocSite.Url(SarifRuleCatalog.WindowFrameRuleId(group.Key)));
            yield return new ReadableBlock.Table(
                [WhereHeader],
                [.. ordered.Select(f => new List<string>
                {
                    Where(f.SourcePath, f.Line, dynamicSqlCallSite: null, pathBase, f.Confidence),
                })]);
        }
    }

    private static IEnumerable<ReadableBlock> WindowFunctionArgument(ScanReport report, int level, string? pathBase)
    {
        if (report.Find<WindowFunctionArgumentFinding>(nameof(WindowFunctionArgumentScanner)).Count == 0)
        {
            yield break;
        }

        yield return new ReadableBlock.Heading(level, $"LAG/LEAD/PERCENTILE_CONT/PERCENTILE_DISC/TABLESAMPLE out-of-range constant arguments ({report.Find<WindowFunctionArgumentFinding>(nameof(WindowFunctionArgumentScanner)).Count})");
        yield return new ReadableBlock.Paragraph(
            "A LAG/LEAD offset argument, a PERCENTILE_CONT/PERCENTILE_DISC percentile argument, or a TABLESAMPLE (... PERCENT) percent argument, constant-folds to a value the engine rejects (a negative offset, a percentile outside the inclusive [0, 1] range, or a percent outside the inclusive [0, 100] range) - oracle-confirmed the statement fails (Msg 8730/Msg 8727/Msg 476) the moment any row reaches the function, or never compiles at all for TABLESAMPLE.");

        foreach (var group in report.Find<WindowFunctionArgumentFinding>(nameof(WindowFunctionArgumentScanner)).GroupBy(f => f.Kind).OrderBy(g => g.Key))
        {
            var ordered = group.ToList();
            yield return new ReadableBlock.Heading(level + 1, $"{HumanizeKindName(group.Key.ToString())} ({ordered.Count})");
            yield return new ReadableBlock.Paragraph(RuleDocSite.Url(SarifRuleCatalog.WindowFunctionArgumentRuleId(group.Key)));
            yield return new ReadableBlock.Table(
                [WhereHeader, "Function", "Argument"],
                [.. ordered.Select(f => new List<string>
                {
                    Where(f.SourcePath, f.Line, dynamicSqlCallSite: null, pathBase, f.Confidence),
                    f.FunctionName,
                    f.ArgumentText,
                })]);
        }
    }

    private static IEnumerable<ReadableBlock> StringSplitArgument(ScanReport report, int level, string? pathBase)
    {
        if (report.Find<StringSplitArgumentFinding>(nameof(StringSplitArgumentScanner)).Count == 0)
        {
            yield break;
        }

        yield return new ReadableBlock.Heading(level, $"STRING_SPLIT separator arguments not exactly one character ({report.Find<StringSplitArgumentFinding>(nameof(StringSplitArgumentScanner)).Count})");
        yield return new ReadableBlock.Paragraph(
            "STRING_SPLIT's separator argument constant-folds to a literal (or literal NULL) whose length is not exactly one character - oracle-confirmed the call fails (Msg 214) at compile/bind time, before any row is read.");

        foreach (var group in report.Find<StringSplitArgumentFinding>(nameof(StringSplitArgumentScanner)).GroupBy(f => f.Kind).OrderBy(g => g.Key))
        {
            var ordered = group.ToList();
            yield return new ReadableBlock.Heading(level + 1, $"{HumanizeKindName(group.Key.ToString())} ({ordered.Count})");
            yield return new ReadableBlock.Paragraph(RuleDocSite.Url(SarifRuleCatalog.StringSplitArgumentRuleId(group.Key)));
            yield return new ReadableBlock.Table(
                [WhereHeader, "Separator"],
                [.. ordered.Select(f => new List<string>
                {
                    Where(f.SourcePath, f.Line, dynamicSqlCallSite: null, pathBase, f.Confidence),
                    f.SeparatorText,
                })]);
        }
    }

    private static IEnumerable<ReadableBlock> BoundedStringBuiltinTruncation(ScanReport report, int level, string? pathBase)
    {
        if (report.Find<BoundedStringBuiltinTruncationFinding>(nameof(BoundedStringBuiltinTruncationScanner)).Count == 0)
        {
            yield break;
        }

        yield return new ReadableBlock.Heading(level, $"REPLICATE/REPLACE/SPACE constant-provable result truncation ({report.Find<BoundedStringBuiltinTruncationFinding>(nameof(BoundedStringBuiltinTruncationScanner)).Count})");
        yield return new ReadableBlock.Paragraph(
            "A REPLICATE/REPLACE call's non-MAX-typed first argument, or a SPACE call's requested count, constant-folds to a result over the function's fixed byte cap (8000 for VARCHAR, 4000 for NVARCHAR) - oracle-confirmed the excess is silently truncated away, with no error.");

        foreach (var group in report.Find<BoundedStringBuiltinTruncationFinding>(nameof(BoundedStringBuiltinTruncationScanner)).GroupBy(f => f.Kind).OrderBy(g => g.Key))
        {
            var ordered = group.ToList();
            yield return new ReadableBlock.Heading(level + 1, $"{HumanizeKindName(group.Key.ToString())} ({ordered.Count})");
            yield return new ReadableBlock.Paragraph(RuleDocSite.Url(SarifRuleCatalog.BoundedStringBuiltinTruncationRuleId(group.Key)));
            yield return new ReadableBlock.Table(
                [WhereHeader, "Function", "Computed length", "Cap"],
                [.. ordered.Select(f => new List<string>
                {
                    Where(f.SourcePath, f.Line, dynamicSqlCallSite: null, pathBase, f.Confidence),
                    f.FunctionName,
                    f.ComputedLength.ToString(CultureInfo.InvariantCulture),
                    f.CapBytes.ToString(CultureInfo.InvariantCulture),
                })]);
        }
    }

    private static IEnumerable<ReadableBlock> WaitFor(ScanReport report, int level, string? pathBase)
    {
        if (report.Find<WaitForFinding>(nameof(WaitForScanner)).Count == 0)
        {
            yield break;
        }

        yield return new ReadableBlock.Heading(level, $"WAITFOR DELAY/TIME ({report.Find<WaitForFinding>(nameof(WaitForScanner)).Count})");
        yield return new ReadableBlock.Paragraph(
            "WAITFOR DELAY/WAITFOR TIME holds the calling worker thread idle for the full delay/until-time - a documented, unconditional cost, worse still when reached inside an open transaction, where any locks that transaction holds stay held for the same duration.");

        yield return new ReadableBlock.Paragraph(RuleDocSite.Url(SarifRuleCatalog.WaitForRuleId));
        yield return new ReadableBlock.Table(
            [WhereHeader, "Inside open transaction?"],
            [.. report.Find<WaitForFinding>(nameof(WaitForScanner)).Select(f => new List<string>
            {
                Where(f.SourcePath, f.Line, dynamicSqlCallSite: null, pathBase, f.Confidence),
                f.IsInsideTransaction ? "Yes" : "No",
            })]);
    }

    private static IEnumerable<ReadableBlock> CursorCloseOnCommit(ScanReport report, int level, string? pathBase)
    {
        if (report.Find<CursorCloseOnCommitFinding>(nameof(CursorCloseOnCommitScanner)).Count == 0)
        {
            yield break;
        }

        yield return new ReadableBlock.Heading(level, $"Cursor silently closed by CURSOR_CLOSE_ON_COMMIT ({report.Find<CursorCloseOnCommitFinding>(nameof(CursorCloseOnCommitScanner)).Count})");
        yield return new ReadableBlock.Paragraph(
            "SET CURSOR_CLOSE_ON_COMMIT ON silently closes every open cursor the instant a COMMIT/full ROLLBACK runs - the next FETCH from that cursor fails at runtime (Msg 16917, \"Cursor is not open\") with no error at the cursor's own OPEN site.");

        yield return new ReadableBlock.Paragraph(RuleDocSite.Url(SarifRuleCatalog.CursorCloseOnCommitRuleId));
        yield return new ReadableBlock.Table(
            [WhereHeader, "Cursor", "Closed by"],
            [.. report.Find<CursorCloseOnCommitFinding>(nameof(CursorCloseOnCommitScanner)).Select(f => new List<string>
            {
                Where(f.SourcePath, f.FetchLine, dynamicSqlCallSite: null, pathBase, f.Confidence),
                f.CursorName,
                f.ClosedByRollback ? "ROLLBACK" : "COMMIT",
            })]);
    }

    private static IEnumerable<ReadableBlock> ViewOrdering(ScanReport report, int level, string? pathBase)
    {
        if (report.Find<ViewOrderingFinding>(nameof(ViewOrderingScanner)).Count == 0)
        {
            yield break;
        }

        yield return new ReadableBlock.Heading(level, $"View/inline TVF ordering not guaranteed ({report.Find<ViewOrderingFinding>(nameof(ViewOrderingScanner)).Count})");
        yield return new ReadableBlock.Paragraph(
            "A view/inline TVF's own outermost query uses TOP/OFFSET ... ORDER BY - T-SQL requires TOP/OFFSET/FOR XML for ORDER BY to appear in a view at all, but the resulting order is never guaranteed to a consumer that doesn't apply its own ORDER BY. TOP (100) PERCENT is the provably meaningless case (100 PERCENT never excludes a row, oracle-confirmed the order is silently discarded); a genuinely row-limiting TOP(N)/OFFSET is a legitimate use whose final output order is still unguaranteed, oracle-observed to sometimes appear ordered only by plan-shape coincidence.");

        foreach (var group in report.Find<ViewOrderingFinding>(nameof(ViewOrderingScanner)).GroupBy(f => f.Kind).OrderBy(g => g.Key))
        {
            var ordered = group.ToList();
            var title = group.Key == ViewOrderingFindingKind.TopPercentOrderByNeverLimits ? "TOP (100) PERCENT (no-op)" : "TOP(N)/OFFSET (order not guaranteed)";
            yield return new ReadableBlock.Heading(level + 1, $"{title} ({ordered.Count})");
            yield return new ReadableBlock.Paragraph(RuleDocSite.Url(SarifRuleCatalog.ViewOrderingRuleId(group.Key)));
            yield return new ReadableBlock.Table(
                [WhereHeader, "Object"],
                [.. ordered.Select(f => new List<string>
                {
                    Where(f.SourcePath, f.Line, dynamicSqlCallSite: null, pathBase, f.Confidence),
                    f.ObjectQualifiedName,
                })]);
        }
    }

    private static IEnumerable<ReadableBlock> TransactionHygiene(ScanReport report, int level, string? pathBase)
    {
        if (report.Find<TransactionHygieneFinding>(nameof(TransactionHygieneScanner)).Count == 0)
        {
            yield break;
        }

        yield return new ReadableBlock.Heading(level, $"Transaction hygiene ({report.Find<TransactionHygieneFinding>(nameof(TransactionHygieneScanner)).Count})");

        foreach (var group in report.Find<TransactionHygieneFinding>(nameof(TransactionHygieneScanner)).GroupBy(f => f.Kind).OrderBy(g => g.Key))
        {
            var ordered = group.ToList();
            var (title, paragraph, columns) = group.Key switch
            {
                TransactionHygieneFindingKind.ImplicitTransactionUnresolvedOnSomePath => (
                    "Unresolved implicit transaction",
                    "SET IMPLICIT_TRANSACTIONS ON silently opens a transaction ahead of the next INSERT/UPDATE/DELETE/MERGE/TRUNCATE/SELECT (with a FROM)/CREATE/ALTER/DROP/GRANT/REVOKE/OPEN CURSOR/FETCH CURSOR with no matching BEGIN TRANSACTION - a RETURN/THROW, or the natural end of the module body, on some statically reachable path with no intervening COMMIT/ROLLBACK leaves @@TRANCOUNT elevated by one the instant the procedure returns, same as an unresolved explicit BEGIN TRANSACTION.",
                    new[] { "Implicit transaction opened at", "Unresolved at" }),
                TransactionHygieneFindingKind.CommitAfterXactAbortDoomsTransaction => (
                    "COMMIT after XACT_ABORT dooms the transaction",
                    "SET XACT_ABORT ON marks a transaction that was already open before a TRY block as uncommittable (XACT_STATE() = -1) the instant an error is caught by the matching CATCH block - oracle-confirmed a COMMIT TRANSACTION reached directly inside that CATCH block always fails with Msg 3930, regardless of the error that triggered the CATCH; only ROLLBACK is possible.",
                    new[] { "Transaction opened at", "Doomed COMMIT at" }),
                _ => (
                    "Unresolved BEGIN TRANSACTION",
                    "A BEGIN TRANSACTION reaches a RETURN/THROW, or the natural end of the module body, on some statically reachable path with no intervening COMMIT/ROLLBACK - oracle-confirmed directly that SQL Server raises Msg 266 and leaves @@TRANCOUNT elevated by one the instant such a procedure returns, holding its locks indefinitely.",
                    new[] { "BEGIN TRANSACTION at", "Unresolved at" }),
            };

            yield return new ReadableBlock.Heading(level + 1, $"{title} ({ordered.Count})");
            yield return new ReadableBlock.Paragraph(paragraph);
            yield return new ReadableBlock.Paragraph(RuleDocSite.Url(SarifRuleCatalog.TransactionHygieneRuleId(group.Key)));
            yield return new ReadableBlock.Table(
                columns,
                [.. ordered.Select(f => new List<string>
                {
                    Where(f.SourcePath, f.BeginTransactionLine, dynamicSqlCallSite: null, pathBase, f.Confidence),
                    $"{f.SourcePath}:{f.UnresolvedExitLine}",
                })]);
        }
    }

    private static IEnumerable<ReadableBlock> MissingStatistics(ScanReport report, int level, string? pathBase)
    {
        if (report.Find<MissingStatisticsFinding>(nameof(MissingStatisticsScanner)).Count == 0)
        {
            yield break;
        }

        yield return new ReadableBlock.Heading(level, $"Predicate columns with no applicable statistic, auto-create disabled ({report.Find<MissingStatisticsFinding>(nameof(MissingStatisticsScanner)).Count})");
        yield return new ReadableBlock.Paragraph(
            "A resolved predicate column has no covering statistic (single-column, or leading key of a multi-column statistic) on its table, and the connected database has AUTO_CREATE_STATISTICS turned off - the engine cannot create one on its own for this predicate.");

        yield return new ReadableBlock.Paragraph(RuleDocSite.Url(SarifRuleCatalog.MissingStatisticsRuleId));
        yield return new ReadableBlock.Table(
            [WhereHeader, TableHeader, "Column"],
            [.. report.Find<MissingStatisticsFinding>(nameof(MissingStatisticsScanner)).Select(f => new List<string>
            {
                Where(f.SourcePath, f.Line, dynamicSqlCallSite: null, pathBase, f.Confidence),
                f.TableQualifiedName,
                f.ColumnName,
            })]);
    }

    private static IEnumerable<ReadableBlock> CompositeIndexLeadingColumn(ScanReport report, int level, string? pathBase)
    {
        if (report.Find<CompositeIndexLeadingColumnFinding>(nameof(CompositeIndexLeadingColumnScanner)).Count == 0)
        {
            yield break;
        }

        yield return new ReadableBlock.Heading(level, $"Composite index leading-column violations ({report.Find<CompositeIndexLeadingColumnFinding>(nameof(CompositeIndexLeadingColumnScanner)).Count})");
        yield return new ReadableBlock.Paragraph(
            "A real composite index's leading key column is never bound anywhere in this statement, while the query genuinely constrains one of that index's later key columns - the index is a single B-tree keyed first by its leading column, so this specific index cannot be seek-used for this predicate at all. Only fires when no other usable index on the table leads with the same violating column either, so this is not an index-recommendation or an overall-query-is-slow claim - just \"this query cannot seek this index\".");

        yield return new ReadableBlock.Paragraph(RuleDocSite.Url(SarifRuleCatalog.CompositeIndexLeadingColumnRuleId));
        yield return new ReadableBlock.Table(
            [WhereHeader, TableHeader, IndexHeader, "Key columns", "Unconstrained leading column", "Violating column"],
            [.. report.Find<CompositeIndexLeadingColumnFinding>(nameof(CompositeIndexLeadingColumnScanner)).Select(f => new List<string>
            {
                Where(f.SourcePath, f.Line, dynamicSqlCallSite: null, pathBase, f.Confidence),
                f.TableQualifiedName,
                f.IndexName ?? "(unnamed)",
                string.Join(", ", f.IndexKeyColumns),
                f.IndexKeyColumns[0],
                $"{f.ViolatingColumnName} (position {f.ViolatingColumnPosition})",
            })]);
    }

    private static IEnumerable<ReadableBlock> IndexHint(ScanReport report, int level, string? pathBase)
    {
        if (report.Find<IndexHintFinding>(nameof(IndexHintScanner)).Count == 0)
        {
            yield break;
        }

        yield return new ReadableBlock.Heading(level, $"INDEX hints naming a nonexistent or non-seekable index ({report.Find<IndexHintFinding>(nameof(IndexHintScanner)).Count})");
        yield return new ReadableBlock.Paragraph(
            "An INDEX(...) table hint either names an index that no longer exists (oracle-confirmed a hard compile error, Msg 308, every time this statement runs) or forces a real index whose own leading key column is never bound anywhere in the statement (oracle-confirmed to degrade the forced access path to a full index scan, since the hint requires this specific index rather than merely suggesting it).");

        foreach (var group in report.Find<IndexHintFinding>(nameof(IndexHintScanner)).GroupBy(f => f.Kind).OrderBy(g => g.Key))
        {
            var ordered = group.ToList();
            var title = group.Key == IndexHintFindingKind.IndexDoesNotExist ? "Index does not exist" : "Leading column never bound";
            yield return new ReadableBlock.Heading(level + 1, $"{title} ({ordered.Count})");
            yield return new ReadableBlock.Paragraph(RuleDocSite.Url(SarifRuleCatalog.IndexHintRuleId(group.Key)));
            yield return new ReadableBlock.Table(
                [WhereHeader, TableHeader, "Hinted index", "Problem"],
                [.. ordered.Select(f => new List<string>
                {
                    Where(f.SourcePath, f.Line, dynamicSqlCallSite: null, pathBase, f.Confidence),
                    f.TableQualifiedName,
                    f.HintedIndexName,
                    f.Kind == IndexHintFindingKind.IndexDoesNotExist ? "Index does not exist" : $"Leading column {f.LeadingColumnName} never bound",
                })]);
        }
    }

    private static IEnumerable<ReadableBlock> SessionDateSetting(ScanReport report, int level, string? pathBase)
    {
        if (report.Find<SessionDateSettingFinding>(nameof(SessionDateSettingScanner)).Count == 0)
        {
            yield break;
        }

        yield return new ReadableBlock.Heading(level, $"SET DATEFORMAT/DATEFIRST mid-module ({report.Find<SessionDateSettingFinding>(nameof(SessionDateSettingScanner)).Count})");
        yield return new ReadableBlock.Paragraph(
            "SET DATEFORMAT/SET DATEFIRST inside a module body changes how a string date literal or DATEPART(weekday, ...) is interpreted for the rest of the session, independent of the caller's own settings - oracle-confirmed the identical literal/date silently means something different depending on which value was set first.");

        foreach (var group in report.Find<SessionDateSettingFinding>(nameof(SessionDateSettingScanner)).GroupBy(f => f.Kind).OrderBy(g => g.Key))
        {
            var ordered = group.ToList();
            var title = group.Key == SessionDateSettingKind.DateFormat ? "DATEFORMAT" : "DATEFIRST";
            yield return new ReadableBlock.Heading(level + 1, $"{title} ({ordered.Count})");
            yield return new ReadableBlock.Paragraph(RuleDocSite.Url(SarifRuleCatalog.SessionDateSettingRuleId(group.Key)));
            yield return new ReadableBlock.Table(
                [WhereHeader],
                [.. ordered.Select(f => new List<string>
                {
                    Where(f.SourcePath, f.Line, dynamicSqlCallSite: null, pathBase, f.Confidence),
                })]);
        }
    }

    private static IEnumerable<ReadableBlock> CartesianJoin(ScanReport report, int level, string? pathBase)
    {
        if (report.Find<CartesianJoinFinding>(nameof(CartesianJoinScanner)).Count == 0)
        {
            yield break;
        }

        yield return new ReadableBlock.Heading(level, $"Cartesian and always-false joins ({report.Find<CartesianJoinFinding>(nameof(CartesianJoinScanner)).Count})");
        yield return new ReadableBlock.Paragraph(
            "A comma-join or explicit CROSS JOIN with no predicate anywhere in the statement - no ON clause, no WHERE clause - connecting the two tables at all is a true cartesian product, distinct from the shipped partial-composite-FK-join rule (which fires when a join predicate exists but is incomplete). An INNER JOIN whose own ON predicate provably never evaluates to TRUE is the complementary defect: instead of matching too many rows, the join can never match any.");

        foreach (var group in report.Find<CartesianJoinFinding>(nameof(CartesianJoinScanner)).GroupBy(f => f.Kind).OrderBy(g => g.Key))
        {
            var ordered = group.ToList();
            var title = group.Key switch
            {
                CartesianJoinKind.ExplicitCrossJoin => "Explicit CROSS JOIN",
                CartesianJoinKind.AlwaysFalseInnerJoinPredicate => "INNER JOIN with an always-false ON predicate",
                _ => "Legacy comma-join",
            };
            yield return new ReadableBlock.Heading(level + 1, $"{title} ({ordered.Count})");
            yield return new ReadableBlock.Paragraph(RuleDocSite.Url(SarifRuleCatalog.CartesianJoinRuleId(group.Key)));
            yield return new ReadableBlock.Table(
                [WhereHeader, "First table", "Second table"],
                [.. ordered.Select(f => new List<string>
                {
                    Where(f.SourcePath, f.Line, dynamicSqlCallSite: null, pathBase, f.Confidence),
                    f.FirstTableQualifiedName,
                    f.SecondTableQualifiedName,
                })]);
        }
    }

    private static IEnumerable<ReadableBlock> TruncateSwallowed(ScanReport report, int level, string? pathBase)
    {
        if (report.Find<TruncateSwallowedFinding>(nameof(TruncateSwallowedScanner)).Count == 0)
        {
            yield break;
        }

        yield return new ReadableBlock.Heading(level, $"TRUNCATE swallowed by an empty/non-rethrowing CATCH ({report.Find<TruncateSwallowedFinding>(nameof(TruncateSwallowedScanner)).Count})");
        yield return new ReadableBlock.Paragraph(
            "TRUNCATE TABLE sits inside a TRY block whose CATCH never THROWs/RAISERRORs - oracle-confirmed a real TRUNCATE failure (e.g. an enforced FK reference, Msg 4712) is silently swallowed here, with execution continuing as if it had succeeded and no error reaching the caller.");

        yield return new ReadableBlock.Paragraph(RuleDocSite.Url(SarifRuleCatalog.TruncateSwallowedRuleId));
        yield return new ReadableBlock.Table(
            [WhereHeader],
            [.. report.Find<TruncateSwallowedFinding>(nameof(TruncateSwallowedScanner)).Select(f => new List<string>
            {
                Where(f.SourcePath, f.Line, dynamicSqlCallSite: null, pathBase, f.Confidence),
            })]);
    }

    private static IEnumerable<ReadableBlock> UnindexedTempTableUsage(ScanReport report, int level, string? pathBase)
    {
        if (report.Find<UnindexedTempTableUsageFinding>(nameof(UnindexedTempTableUsageScanner)).Count == 0)
        {
            yield break;
        }

        yield return new ReadableBlock.Heading(level, $"Unindexed SELECT INTO temp table usage ({report.Find<UnindexedTempTableUsageFinding>(nameof(UnindexedTempTableUsageScanner)).Count})");
        yield return new ReadableBlock.Paragraph(
            "A SELECT...INTO #temp table is later joined or filtered by a WHERE predicate in the same batch/procedure scope, but no index was ever created on it - oracle-confirmed this forces a full scan of the temp table, with no seek alternative possible at all.");

        foreach (var group in report.Find<UnindexedTempTableUsageFinding>(nameof(UnindexedTempTableUsageScanner)).GroupBy(f => f.Kind).OrderBy(g => g.Key))
        {
            var ordered = group.ToList();
            var title = group.Key == UnindexedTempTableUsageKind.JoinOperand ? "JOIN operand" : "Filtered in WHERE";
            yield return new ReadableBlock.Heading(level + 1, $"{title} ({ordered.Count})");
            yield return new ReadableBlock.Paragraph(RuleDocSite.Url(SarifRuleCatalog.UnindexedTempTableUsageRuleId(group.Key)));
            yield return new ReadableBlock.Table(
                [WhereHeader, "Temp table"],
                [.. ordered.Select(f => new List<string>
                {
                    Where(f.SourcePath, f.UsageLine, dynamicSqlCallSite: null, pathBase, f.Confidence),
                    f.TempTableQualifiedName,
                })]);
        }
    }

    private static IEnumerable<ReadableBlock> OutputParameter(ScanReport report, int level, string? pathBase)
    {
        if (report.Find<OutputParameterFinding>(nameof(OutputParameterScanner)).Count == 0)
        {
            yield break;
        }

        yield return new ReadableBlock.Heading(level, $"Unassigned OUTPUT parameters ({report.Find<OutputParameterFinding>(nameof(OutputParameterScanner)).Count})");
        yield return new ReadableBlock.Paragraph(
            "An OUTPUT parameter is not assigned on some statically reachable path - oracle-confirmed a caller's own variable is left completely unchanged by the call on that path (not reset to NULL), so a reused caller variable can silently carry stale data from a previous, unrelated call.");

        yield return new ReadableBlock.Paragraph(RuleDocSite.Url(SarifRuleCatalog.OutputParameterRuleId));
        yield return new ReadableBlock.Table(
            ["Procedure at", ParameterHeader, "Unresolved at"],
            [.. report.Find<OutputParameterFinding>(nameof(OutputParameterScanner)).Select(f => new List<string>
            {
                Where(f.SourcePath, f.ProcedureLine, dynamicSqlCallSite: null, pathBase, f.Confidence),
                f.ParameterName,
                $"{f.SourcePath}:{f.UnresolvedExitLine}",
            })]);
    }

    private static IEnumerable<ReadableBlock> DatabaseConfiguration(ScanReport report, int level)
    {
        if (report.Find<DatabaseConfigurationFinding>(DatabaseConfigurationRuleId).Count == 0)
        {
            yield break;
        }

        yield return new ReadableBlock.Heading(level, $"Database-level configuration flags ({report.Find<DatabaseConfigurationFinding>(DatabaseConfigurationRuleId).Count})");
        yield return new ReadableBlock.Paragraph(
            "Read once per scan run directly from sys.databases/sys.database_query_store_options - a database-granularity fact, not a per-module one. PAGE_VERIFY/AUTO_SHRINK/AUTO_CLOSE/TARGET_RECOVERY_TIME/AUTO_CREATE_STATISTICS/AUTO_UPDATE_STATISTICS/compatibility level (compared against the connected engine instance's own current default, read live from the model system database) are well-established anti-patterns; the two Query Store flags are informational since whether Query Store should be on is a real operational choice.");

        foreach (var group in report.Find<DatabaseConfigurationFinding>(DatabaseConfigurationRuleId).GroupBy(f => f.Kind).OrderBy(g => g.Key))
        {
            var ordered = group.ToList();
            yield return new ReadableBlock.Heading(level + 1, $"{HumanizeKindName(group.Key.ToString())} ({ordered.Count})");
            yield return new ReadableBlock.Paragraph(RuleDocSite.Url(SarifRuleCatalog.DatabaseConfigurationRuleId(group.Key)));
            yield return new ReadableBlock.Table(
                ["Database", "Flag"],
                [.. ordered.Select(f => new List<string>
                {
                    f.DatabaseName,
                    DatabaseConfigurationFlagLabel(f),
                })]);
        }
    }

    private static string DatabaseConfigurationFlagLabel(DatabaseConfigurationFinding finding) => finding.Kind switch
    {
        DatabaseConfigurationFindingKind.PageVerifyNotChecksum => "PAGE_VERIFY <> CHECKSUM",
        DatabaseConfigurationFindingKind.AutoShrinkOn => "AUTO_SHRINK = ON",
        DatabaseConfigurationFindingKind.AutoCloseOn => "AUTO_CLOSE = ON",
        DatabaseConfigurationFindingKind.TargetRecoveryTimeUnset => "TARGET_RECOVERY_TIME unset (0)",
        DatabaseConfigurationFindingKind.QueryStoreNotReadWrite => "Query Store not READ_WRITE",
        DatabaseConfigurationFindingKind.QueryStoreCaptureModeNotAuto => "Query Store capture mode <> AUTO",
        DatabaseConfigurationFindingKind.AutoCreateStatisticsOff => "AUTO_CREATE_STATISTICS = OFF",
        DatabaseConfigurationFindingKind.AutoUpdateStatisticsOff => "AUTO_UPDATE_STATISTICS = OFF",
        DatabaseConfigurationFindingKind.CompatibilityLevelBehindEngineDefault => "Compatibility level behind engine default",
        DatabaseConfigurationFindingKind.SpatialPersistedComputedColumnDisabledOnCompatibilityLevelChange => $"{finding.AffectedObjectName} disabled at compatibility level {finding.TargetCompatibilityLevel} ({finding.Dependency})",
        _ => finding.Kind.ToString(),
    };

    private static IEnumerable<ReadableBlock> PartialCompositeForeignKeyJoin(ScanReport report, int level, string? pathBase)
    {
        if (report.Find<PartialCompositeForeignKeyJoinFinding>(nameof(PartialCompositeForeignKeyJoinScanner)).Count == 0)
        {
            yield break;
        }

        yield return new ReadableBlock.Heading(level, $"JOINs matching part of a composite foreign key ({report.Find<PartialCompositeForeignKeyJoinFinding>(nameof(PartialCompositeForeignKeyJoinScanner)).Count})");
        yield return new ReadableBlock.Paragraph(
            "A CORRECTNESS and plan defect, not a lost seek: this join equates some but not all of a real composite foreign key's column pairs, and the omitted column(s) are not covered anywhere else in the statement - a parent row can match more than one child row than the declared relationship allows, silently multiplying rows through the join. Reported at MEDIUM confidence by default: a narrower join can be a genuine, deliberate fan-out (e.g. joining every historical revision), which static analysis alone cannot always tell apart from a forgotten column - review each one rather than treating it as a certain bug.");

        yield return new ReadableBlock.Paragraph(RuleDocSite.Url(SarifRuleCatalog.PartialCompositeForeignKeyJoinRuleId));
        yield return new ReadableBlock.Table(
            [WhereHeader, ConstraintHeader, "Tables", "Matched columns", "Missing columns"],
            [.. report.Find<PartialCompositeForeignKeyJoinFinding>(nameof(PartialCompositeForeignKeyJoinScanner)).Select(f => new List<string>
            {
                Where(f.SourcePath, f.Line, dynamicSqlCallSite: null, pathBase, f.Confidence),
                f.ConstraintName,
                $"{f.ParentTableQualifiedName} -> {f.ReferencedTableQualifiedName}",
                string.Join(", ", f.MatchedColumnPairs.Select(p => $"{p.ParentColumnName}={p.ReferencedColumnName}")),
                string.Join(", ", f.MissingColumnPairs.Select(p => $"{p.ParentColumnName}={p.ReferencedColumnName}")),
            })]);
    }

    private static IEnumerable<ReadableBlock> OuterJoinPredicateCollapse(ScanReport report, int level, string? pathBase)
    {
        if (report.Find<OuterJoinPredicateCollapseFinding>(nameof(OuterJoinPredicateCollapseScanner)).Count == 0)
        {
            yield break;
        }

        yield return new ReadableBlock.Heading(level, $"OUTER JOIN predicates that collapse to an INNER JOIN ({report.Find<OuterJoinPredicateCollapseFinding>(nameof(OuterJoinPredicateCollapseScanner)).Count})");
        yield return new ReadableBlock.Paragraph(
            "A WHERE-clause predicate rejects NULL on a column from an OUTER JOIN's null-supplying side, with no OR ... IS NULL guard anywhere in the same AND-conjunct - every row where the join found no match has that column NULL, so the predicate discards it, silently making the OUTER JOIN behave exactly like an INNER JOIN.");

        yield return new ReadableBlock.Paragraph(RuleDocSite.Url(SarifRuleCatalog.OuterJoinPredicateCollapseRuleId));
        yield return new ReadableBlock.Table(
            [WhereHeader, "Join kind", "Table", "Column"],
            [.. report.Find<OuterJoinPredicateCollapseFinding>(nameof(OuterJoinPredicateCollapseScanner)).Select(f => new List<string>
            {
                Where(f.SourcePath, f.Line, dynamicSqlCallSite: null, pathBase, f.Confidence),
                f.Kind switch
                {
                    OuterJoinPredicateCollapseKind.LeftOuterJoin => "LEFT OUTER JOIN",
                    OuterJoinPredicateCollapseKind.RightOuterJoin => "RIGHT OUTER JOIN",
                    _ => "FULL OUTER JOIN",
                },
                f.NullSupplyingTableQualifiedName,
                f.ColumnName,
            })]);
    }

    private static IEnumerable<ReadableBlock> SetOption(ScanReport report, int level, string? pathBase)
    {
        if (report.Find<SetOptionFinding>(nameof(SetOptionScanner)).Count == 0)
        {
            yield break;
        }

        yield return new ReadableBlock.Heading(level, $"SET options silently disabling a filtered index/indexed view ({report.Find<SetOptionFinding>(nameof(SetOptionScanner)).Count})");
        yield return new ReadableBlock.Paragraph(
            "QUOTED_IDENTIFIER OFF, ANSI_NULLS OFF, SET NUMERIC_ROUNDABORT ON, SET ANSI_WARNINGS OFF, and SET CONCAT_NULL_YIELDS_NULL OFF each independently make a filtered index or an indexed view unusable by the optimizer, silently falling back to a base-table/heap scan - none shows up in the query text as anything resembling a predicate, so the plan consequence is invisible at the call site. Oracle-confirmed directly (real seeded data, both a filtered index and an indexed view). Only reported when this module's own body was proven to touch a filtered index or an indexed view (directly, or through a referenced view however many layers down) - see each row's own touched object. SET ARITHABORT OFF was investigated and deliberately excluded: oracle-probed directly, it changed neither plan at all on this engine version/edition, contradicting the checklist's original premise that lumped all six options together.");

        foreach (var group in report.Find<SetOptionFinding>(nameof(SetOptionScanner))
            .GroupBy(f => f.Kind)
            .OrderBy(g => g.Key))
        {
            var ordered = group.OrderBy(f => f.SourcePath, StringComparer.Ordinal).ThenBy(f => f.Line).ThenBy(f => f.Column).ToList();

            yield return new ReadableBlock.Heading(level + 1, $"{SetOptionTitle(group.Key)} ({ordered.Count})");
            yield return new ReadableBlock.Paragraph(RuleDocSite.Url(SarifRuleCatalog.SetOptionRuleId(group.Key)));
            yield return new ReadableBlock.Table(
                [WhereHeader, ModuleHeader, "Touched object", "Kind"],
                [.. ordered.Select(f => new List<string>
                {
                    Where(f.SourcePath, f.Line, dynamicSqlCallSite: null, pathBase, f.Confidence),
                    f.ModuleQualifiedName,
                    DescribeTouchedObject(f.TouchedObjectQualifiedName, f.TouchedIndexName),
                    f.TouchedIsIndexedView ? "indexed view" : "filtered index",
                })]);
        }
    }

    private static string DescribeTouchedObject(string? qualifiedName, string? indexName)
    {
        if (qualifiedName is null)
        {
            return UnknownDisplay;
        }

        return indexName is { } idx ? $"{qualifiedName}.{idx}" : qualifiedName;
    }

    private static string SetOptionTitle(SetOptionFindingKind kind) => kind switch
    {
        SetOptionFindingKind.QuotedIdentifierOffBlocksIndexedFeature => "Module compiled under QUOTED_IDENTIFIER OFF",
        SetOptionFindingKind.AnsiNullsOffBlocksIndexedFeature => "Module compiled under ANSI_NULLS OFF",
        SetOptionFindingKind.NumericRoundabortOnBlocksIndexedFeature => "SET NUMERIC_ROUNDABORT ON",
        SetOptionFindingKind.AnsiWarningsOffBlocksIndexedFeature => "SET ANSI_WARNINGS OFF",
        SetOptionFindingKind.ConcatNullYieldsNullOffBlocksIndexedFeature => "SET CONCAT_NULL_YIELDS_NULL OFF",
        SetOptionFindingKind.AnsiPaddingOffBlocksIndexedFeature => "SET ANSI_PADDING OFF",
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "Unhandled SetOptionFindingKind."),
    };

    private static string ScalarUdfTitle(ScalarUdfFindingKind kind) => kind switch
    {
        ScalarUdfFindingKind.PredicateInvocation => "Called in a predicate (non-sargable, per-row)",
        ScalarUdfFindingKind.NestedUnderViewOrTvf => "Reached through a view/iTVF layer",
        ScalarUdfFindingKind.SchemaDependency => "Called from a computed column/DEFAULT/CHECK constraint",
        ScalarUdfFindingKind.ProjectionInvocation => "Called outside a predicate (per-row, sargability unaffected)",
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "Unhandled ScalarUdfFindingKind."),
    };

    private static string ScalarUdfInlineabilityDisplay(ScalarUdfFinding finding) => finding.Inlineability switch
    {
        ScalarUdfInlineability.Inlineable => "yes (2019+ FROID)",
        ScalarUdfInlineability.NotInlineable => "no",
        _ => UnknownDisplay,
    };

    private static string ScalarUdfDetail(ScalarUdfFinding finding)
    {
        var parts = new List<string>();
        if (finding.InlineabilityBlocker is { Length: > 0 } blocker)
        {
            parts.Add(blocker);
        }

        if (finding.ConstantArgumentsNotFolded)
        {
            parts.Add("non-schemabound, literal arguments not constant-folded");
        }

        if (finding.UdfKind == ScalarUdfKind.Clr && finding.ClrDataAccess is { } dataAccess)
        {
            parts.Add(dataAccess ? "CLR, data access" : "CLR, no data access");
        }

        return parts.Count == 0 ? finding.ReferenceFragmentText ?? "-" : string.Join("; ", parts);
    }

    private static IEnumerable<ReadableBlock> DynamicSql(ScanReport report, int level, string? pathBase, ReadableVerbosity verbosity)
    {
        var unresolved = report.Find<DynamicSqlFinding>(DynamicSqlRuleId)
            .Where(f => f.Outcome != DynamicSqlOutcome.AnalyzedLiteral)
            .ToList();

        if (unresolved.Count == 0)
        {
            yield break;
        }

        var summary = report.DynamicSqlSummary;
        yield return new ReadableBlock.Heading(level, $"Dynamic SQL not fully analyzed ({unresolved.Count})");
        yield return new ReadableBlock.Paragraph(
            $"{summary.AnalyzedCount} of {Count(summary.TotalCallSites, "dynamic SQL call site")} had a provably-constant argument and were analyzed like ordinary SQL. " +
            "The rest are listed here rather than counted as clean: whatever wasn't examined - the whole argument, or (for a partially-analyzed site) just the elided fragment - is never silently assumed safe.");

        if (verbosity == ReadableVerbosity.Brief)
        {
            yield return BriefPointer(unresolved.Count, "call site");
            yield break;
        }

        foreach (var group in unresolved.GroupBy(f => f.Outcome).OrderBy(g => g.Key))
        {
            var ordered = group.ToList();
            yield return new ReadableBlock.Heading(level + 1, $"{DynamicSqlOutcomeLabel(group.Key)} ({ordered.Count})");
            yield return new ReadableBlock.Paragraph(RuleDocSite.Url(SarifRuleCatalog.DynamicSqlRuleId(group.Key)));
            yield return new ReadableBlock.Table(
                [WhereHeader, "Reason"],
                [.. ordered.Select(f => new List<string>
                {
                    Where(f.SourcePath, f.Line, null, pathBase),
                    f.Reason ?? "-",
                })]);
        }
    }

    private static string DynamicSqlOutcomeLabel(DynamicSqlOutcome outcome) => outcome switch
    {
        DynamicSqlOutcome.InnerParseFailed => "constant, but did not parse as T-SQL",
        DynamicSqlOutcome.PartiallyAnalyzed => "partially analyzed - an unresolvable fragment was elided",
        _ => "not provably constant",
    };

    private static IEnumerable<ReadableBlock> ParseFailures(ScanReport report, int level, string? pathBase, ReadableVerbosity verbosity)
    {
        var failed = report.ParseHealth.Files.Where(f => f.Errors.Count > 0).ToList();
        if (failed.Count == 0)
        {
            yield break;
        }

        yield return new ReadableBlock.Heading(level, $"Files with parse errors ({failed.Count})");
        yield return new ReadableBlock.Paragraph(
            "A batch containing a syntax error is dropped, not the whole file, so these files still contributed whatever else they contained. What the failing batches held was never analyzed - if the rate here is high, the files are likely not T-SQL at all.");

        if (verbosity == ReadableVerbosity.Brief)
        {
            yield return BriefPointer(failed.Count, "file");
            yield break;
        }

        yield return new ReadableBlock.Table(
            ["File", "Errors", "Batches kept", "First error"],
            [.. failed.Select(f => new List<string>
            {
                Relative(f.Path, pathBase),
                f.Errors.Count.ToString(CultureInfo.InvariantCulture),
                f.BatchCount.ToString(CultureInfo.InvariantCulture),
                $"line {f.Errors[0].Line.ToString(CultureInfo.InvariantCulture)}: {f.Errors[0].Message}",
            })]);
    }

    private static IEnumerable<ReadableBlock> UnanalyzedObjects(ScanReport report, int level, string? pathBase, ReadableVerbosity verbosity)
    {
        var unanalyzed = report.ParseHealth.Files.SelectMany(f => f.UnanalyzedBatches).ToList();
        if (unanalyzed.Count == 0)
        {
            yield break;
        }

        yield return new ReadableBlock.Heading(level, $"Unanalyzed objects - dropped batches ({unanalyzed.Count})");
        yield return new ReadableBlock.Paragraph(
            "Each of these batches failed to parse and was dropped entirely (see \"Files with parse errors\" above) - the object it was defining, if any, received zero analysis. This is a coverage gap, not a finding: an object listed here was never examined, not confirmed clean.");

        if (verbosity == ReadableVerbosity.Brief)
        {
            yield return BriefPointer(unanalyzed.Count, "unanalyzed object");
            yield break;
        }

        yield return new ReadableBlock.Table(
            [WhereHeader, "Kind", "Object"],
            [.. unanalyzed.Select(u => new List<string>
            {
                Where(u.SourcePath, u.StartLine, null, pathBase),
                UnanalyzedObjectKindLabel(u.Kind),
                u.ObjectName ?? "(unidentified)",
            })]);
    }

    private static string UnanalyzedObjectKindLabel(UnanalyzedObjectKind kind) => kind switch
    {
        UnanalyzedObjectKind.Procedure => "procedure",
        UnanalyzedObjectKind.View => "view",
        UnanalyzedObjectKind.Function => "function",
        UnanalyzedObjectKind.Trigger => "trigger",
        UnanalyzedObjectKind.Table => "table",
        _ => "unidentified",
    };

    private static IEnumerable<ReadableBlock> SkippedConstructs(ScanReport report, int level)
    {
        var summary = report.SkippedConstructSummary;
        if (summary.TotalCount == 0)
        {
            yield break;
        }

        yield return new ReadableBlock.Heading(level, $"Constructs skipped as out of scope ({summary.TotalCount})");
        yield return new ReadableBlock.Paragraph(
            "Parsed, recognised, and deliberately not analyzed. They are neither findings nor evidence of cleanliness - they are the part of the scanned SQL this tool does not claim to cover.");
        yield return new ReadableBlock.Table(
            ["Construct", "Pass", "Count"],
            [.. report.SkippedConstructs
                .GroupBy(s => (s.ConstructKind, s.Pass))
                .OrderByDescending(g => g.Count())
                .ThenBy(g => g.Key.ConstructKind, StringComparer.Ordinal)
                .Select(g => new List<string>
                {
                    g.Key.ConstructKind,
                    DescribePass(g.Key.Pass),
                    g.Count().ToString(CultureInfo.InvariantCulture),
                })]);
    }

    private static string DescribePass(AnalysisPass pass) => pass switch
    {
        AnalysisPass.Catalog => "catalog",
        AnalysisPass.Lineage => "lineage",
        _ => "predicates",
    };

    private static string HumanizeKindName(string kindName)
    {
        var words = new System.Text.StringBuilder();
        for (var i = 0; i < kindName.Length; i++)
        {
            var c = kindName[i];
            if (i > 0 && char.IsUpper(c) && char.IsLower(kindName[i - 1]))
            {
                words.Append(' ');
            }

            words.Append(i == 0 ? char.ToUpperInvariant(c) : char.ToLowerInvariant(c));
        }

        return words.ToString();
    }

    private static string Where(string sourcePath, int line, SourceSpan? dynamicSqlCallSite, string? pathBase, FindingConfidence confidence = FindingConfidence.High)
    {
        var location = $"{Relative(sourcePath, pathBase)}:{line.ToString(CultureInfo.InvariantCulture)}";

        var withCallSite = dynamicSqlCallSite is { } span && (span.SourcePath != sourcePath || span.Line != line)
            ? $"{location} (in dynamic SQL run at {Relative(span.SourcePath, pathBase)}:{span.Line.ToString(CultureInfo.InvariantCulture)})"
            : location;

        return confidence == FindingConfidence.High ? withCallSite : $"{withCallSite} [{confidence.ToString().ToUpperInvariant()} CONFIDENCE]";
    }

    private static string Relative(string path, string? pathBase)
    {
        if (string.IsNullOrEmpty(pathBase))
        {
            return path;
        }

        var normalizedBase = pathBase.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        if (normalizedBase.Length == 0 || !path.StartsWith(normalizedBase, StringComparison.Ordinal))
        {
            return path;
        }

        var remainder = path[normalizedBase.Length..];
        return remainder.Length > 0 && (remainder[0] == Path.DirectorySeparatorChar || remainder[0] == Path.AltDirectorySeparatorChar)
            ? remainder[1..]
            : path;
    }

    private static string DescribeTransformationSite(TransformationSite site, string? pathBase) =>
        site.SourcePath is null
            ? site.Description
            : $"{site.Description} at {Relative(site.SourcePath, pathBase)}:{site.Line.ToString(CultureInfo.InvariantCulture)}";

    private static string DescribeType(SqlType? type) => type?.ToString() ?? UnknownDisplay;

    private static string DescribeOperand(PredicateOperand operand) => operand switch
    {
        PredicateOperand.Column column => $"{column.TableQualifiedName}.{column.ColumnName} ({DescribeType(column.Type)})",
        PredicateOperand.Value { IsLiteral: true, LiteralText: { } text } value => $"{text} ({DescribeType(value.Type)})",
        PredicateOperand.Value value => DescribeType(value.Type),
        _ => UnknownDisplay,
    };

    private static string DescribeOrigin(PredicateOperand.Column column, string? pathBase)
    {
        if (column.Depth == 0)
        {
            return "direct table predicate";
        }

        var layers = column.Depth == 1 ? "1 view layer" : $"{column.Depth.ToString(CultureInfo.InvariantCulture)} view layers";
        string via;
        if (column.ImmediateRelationQualifiedName is { } relation)
        {
            var columnSuffix = column.ImmediateColumnName is { } name ? $".{name}" : string.Empty;
            via = $" via {relation}{columnSuffix}";
        }
        else
        {
            via = string.Empty;
        }

        var origin = ProvenanceOrigin(column.Provenance) is { } site
            ? $", introduced at {Relative(site.Path, pathBase)}:{site.Line.ToString(CultureInfo.InvariantCulture)}"
            : string.Empty;

        return $"{layers}{via}{origin}";
    }

    private static (string Path, int Line)? ProvenanceOrigin(ColumnProvenance provenance) => provenance switch
    {
        ColumnProvenance.Cast { OriginSourcePath: { } path } cast => (path, cast.OriginLine),
        ColumnProvenance.Cast cast => ProvenanceOrigin(cast.Inner),
        ColumnProvenance.Expression { OriginSourcePath: { } path } expression => (path, expression.OriginLine),
        ColumnProvenance.Expression expression => expression.Inputs.Select(ProvenanceOrigin).FirstOrDefault(o => o is not null),
        ColumnProvenance.Union union => union.Branches.Select(ProvenanceOrigin).FirstOrDefault(o => o is not null),
        _ => null,
    };

    internal static string Percent(double rate) =>
        $"{(rate * 100).ToString("0.0", CultureInfo.InvariantCulture)}%";

    private static string Count(int value, string noun) =>
        $"{value.ToString(CultureInfo.InvariantCulture)} {noun}{(value == 1 ? string.Empty : "s")}";
}
