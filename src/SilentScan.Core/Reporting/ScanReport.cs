using SilentScan.Core.Diagnostics;
using SilentScan.Core.Predicates;

namespace SilentScan.Core.Reporting;

public sealed record ScanReport(
    ParseHealthReport ParseHealth,
    IReadOnlyList<SargabilityFinding> Tier1Findings,
    IReadOnlyList<TypedPredicateFinding> TypedFindings,
    IReadOnlyList<DynamicSqlFinding> DynamicSqlFindings,
    IReadOnlyList<ExpressionDerivedFinding> ExpressionDerivedFindings,
    IReadOnlyList<CollationConflictFinding> CollationConflictFindings,
    IReadOnlyList<WriteLossFinding> WriteLossFindings,
    IReadOnlyList<TvfFenceFinding> TvfFenceFindings,
    IReadOnlyList<ScalarUdfFinding> ScalarUdfFindings,
    IReadOnlyList<ColumnCollationDriftFinding> ColumnCollationDriftFindings,
    IReadOnlyList<CrossTableTypeDriftFinding> CrossTableTypeDriftFindings,
    IReadOnlyList<ProcCallArgumentMismatchFinding> ProcCallArgumentMismatchFindings,
    IReadOnlyList<TemporalBoundaryPrecisionFinding> TemporalBoundaryFindings,
    IReadOnlyList<MaxTypedColumnFinding> MaxTypedColumnFindings,
    IReadOnlyList<OversizedParameterFinding> OversizedParameterFindings,
    IReadOnlyList<UnderLengthParameterFinding> UnderLengthParameterFindings,
    IReadOnlyList<AnsiPaddingMismatchFinding> AnsiPaddingMismatchFindings,
    IReadOnlyList<PartialCompositeForeignKeyJoinFinding> PartialCompositeForeignKeyJoinFindings,
    IReadOnlyList<SetOptionFinding> SetOptionFindings,
    IReadOnlyList<CatchAllPredicateFinding> CatchAllPredicateFindings,
    IReadOnlyList<LocalVariablePredicateFinding> LocalVariablePredicateFindings,
    IReadOnlyList<NotInNullableSubqueryFinding> NotInNullableSubqueryFindings,
    IReadOnlyList<NonUniqueUpdateSourceFinding> NonUniqueUpdateSourceFindings,
    IReadOnlyList<ForcedSerialFinding> ForcedSerialFindings,
    IReadOnlyList<UntrustedConstraintFinding> UntrustedConstraintFindings,
    IReadOnlyList<CascadingForeignKeyFinding> CascadingForeignKeyFindings,
    IReadOnlyList<MultiReferencedCteFinding> MultiReferencedCteFindings,
    IReadOnlyList<NestedViewDepthFinding> NestedViewDepthFindings,
    IReadOnlyList<PostExpansionJoinWidthFinding> PostExpansionJoinWidthFindings,
    IReadOnlyList<SelectStarViewFinding> SelectStarViewFindings,
    IReadOnlyList<UnparameterizedDynamicSqlFinding> UnparameterizedDynamicSqlFindings,
    IReadOnlyList<NonPersistedComputedColumnFinding> NonPersistedComputedColumnFindings,
    IReadOnlyList<TempTableExecShapeFinding> TempTableExecShapeFindings,
    IReadOnlyList<SelfReferencingDmlFinding> SelfReferencingDmlFindings,
    IReadOnlyList<TemporalTableHistoryIndexGapFinding> TemporalTableHistoryIndexGapFindings,
    IReadOnlyList<ModuleCompileFlagFinding> ModuleCompileFlagFindings,
    IReadOnlyList<WindowFrameFinding> WindowFrameFindings,
    IReadOnlyList<WaitForFinding> WaitForFindings,
    IReadOnlyList<ViewOrderingFinding> ViewOrderingFindings,
    IReadOnlyList<TransactionHygieneFinding> TransactionHygieneFindings,
    IReadOnlyList<CompositeIndexLeadingColumnFinding> CompositeIndexLeadingColumnFindings,
    IReadOnlyList<IndexHintFinding> IndexHintFindings,
    IReadOnlyList<SessionDateSettingFinding> SessionDateSettingFindings,
    IReadOnlyList<CartesianJoinFinding> CartesianJoinFindings,
    IReadOnlyList<UndersizedDeclarationFinding> UndersizedDeclarationFindings,
    IReadOnlyList<TruncateSwallowedFinding> TruncateSwallowedFindings,
    IReadOnlyList<UnindexedTempTableUsageFinding> UnindexedTempTableUsageFindings,
    IReadOnlyList<OutputParameterFinding> OutputParameterFindings,
    IReadOnlyList<DatabaseConfigurationFinding> DatabaseConfigurationFindings,
    IReadOnlyList<ParameterReassignmentPredicateFinding> ParameterReassignmentPredicateFindings,
    IReadOnlyList<CodeMetricFinding> CodeMetricFindings,
    IReadOnlyList<FormattingFinding> FormattingFindings,
    IReadOnlyList<NamingFinding> NamingFindings,
    IReadOnlyList<DeadCodeFinding> DeadCodeFindings,
    IReadOnlyList<DuplicationFinding> DuplicationFindings,
    IReadOnlyList<DeprecatedSyntaxFinding> DeprecatedSyntaxFindings,
    IReadOnlyList<StatementShapeFinding> StatementShapeFindings,
    IReadOnlyList<SkippedConstruct> SkippedConstructs,
    SkippedConstructSummary SkippedConstructSummary,
    TypedPredicateSummary TypedPredicateSummary,
    DynamicSqlSummary DynamicSqlSummary,
    int SchemaVersion = ScanReport.CurrentSchemaVersion)
{
    /// <summary>
    /// Bumped whenever a breaking change is made to this report's own shape or to any finding
    /// record it carries (a field renamed/removed, an enum member's meaning changed) - CLAUDE.md:
    /// "Findings schema is versioned JSON." Before this field existed, nothing in the emitted
    /// JSON let a consumer tell one tool version's output from another's. Bumped to 3 for the
    /// new WriteLossFindings stream (roadmap Phase E1). Bumped to 4 for the
    /// <see cref="Predicates.FindingConfidence"/> field added to every finding record - additive
    /// and defaulted to High, but a consumer that only checked schema version to decide whether
    /// a new field might exist deserves the same signal any other new field gets. Bumped to 5
    /// for the new <see cref="TvfFenceFindings"/> stream (docs/detection-checklist.md Tier 1
    /// #2, MSTVF-as-fence). Bumped to 6 for the new <see cref="ScalarUdfFindings"/> stream
    /// (docs/detection-checklist.md Tier 1 #1, scalar UDF). Bumped to 7 for the new
    /// <see cref="ColumnCollationDriftFindings"/> stream (docs/detection-checklist.md Tier 1
    /// "Join-key and cross-object type/collation mismatch": column/temp-object collation !=
    /// database collation). Bumped to 8 for the new <see cref="CrossTableTypeDriftFindings"/>
    /// stream (same Tier 1 section: FK-linked cross-table type drift). Bumped to 9 for the new
    /// <see cref="ProcCallArgumentMismatchFindings"/> stream (same Tier 1 section: call-boundary
    /// argument mismatch). Bumped to 10 for the new <see cref="TemporalBoundaryFindings"/> stream
    /// (docs/detection-checklist.md Tier 1 "Type-aware upgrade of the sargability stream": BETWEEN
    /// end-of-period boundary - a correctness finding, not a sargability one, but shares the same
    /// scope-resolution walk as Tier1Findings). Bumped to 11 for the new
    /// <see cref="MaxTypedColumnFindings"/> and <see cref="OversizedParameterFindings"/> streams
    /// (docs/detection-checklist.md Tier 1 "Oversized and MAX-typed parameters"). Bumped to 12
    /// for the new <see cref="PartialCompositeForeignKeyJoinFindings"/> stream (docs/detection-
    /// checklist.md Tier 1 "Join predicate incomplete vs. the backing foreign key"). Bumped to 13
    /// for the new <see cref="SetOptionFindings"/> stream (docs/detection-checklist.md Tier 1
    /// "SET options that silently disable plan features"). Bumped to 14 for the new
    /// <see cref="UnderLengthParameterFindings"/> stream (docs/detection-checklist.md Tier 1
    /// "Under-length and length-defaulted string declarations"). Bumped to 15 for the new
    /// <see cref="AnsiPaddingMismatchFindings"/> stream (docs/detection-checklist.md Tier 1
    /// "SET options that silently disable plan features": "ANSI_PADDING OFF as a second,
    /// independent finding"). Bumped to 16 for the new <see cref="CatchAllPredicateFindings"/>
    /// and <see cref="LocalVariablePredicateFindings"/> streams (docs/detection-checklist.md
    /// Tier 2 "Catch-all / kitchen-sink predicates" and "Local-variable predicates"). Bumped to
    /// 17 for the new <see cref="NotInNullableSubqueryFindings"/> stream (docs/detection-
    /// checklist.md Tier 2 "NOT IN over a nullable subquery column"). Bumped to 18 for the new
    /// <see cref="NonUniqueUpdateSourceFindings"/> stream (docs/detection-checklist.md Tier 2
    /// "UPDATE ... FROM without source uniqueness"). Bumped to 19 for the new
    /// <see cref="ForcedSerialFindings"/> stream (docs/detection-checklist.md Tier 2
    /// "Forced-serial construct inventory"). Bumped to 20 for the new
    /// <see cref="UntrustedConstraintFindings"/> and <see cref="CascadingForeignKeyFindings"/>
    /// streams (docs/detection-checklist.md Tier 2 "Lineage-metric findings": "Untrusted (WITH
    /// NOCHECK) FK/CHECK constraints" and "Cascading FK actions"). Bumped to 21 for the new
    /// <see cref="MultiReferencedCteFindings"/> stream (docs/detection-checklist.md Tier 2
    /// "Lineage-metric findings": "Multi-referenced CTE"). Bumped to 22 for the new
    /// <see cref="NestedViewDepthFindings"/> and <see cref="PostExpansionJoinWidthFindings"/>
    /// streams (docs/detection-checklist.md Tier 2 "Lineage-metric findings": "Nested-view depth
    /// report" and "Post-expansion join width"). Bumped to 23 for the new
    /// <see cref="SelectStarViewFindings"/> stream (docs/detection-checklist.md Tier 2
    /// "Lineage-metric findings": "SELECT * inside a view or inline TVF"). Bumped to 24 for the
    /// new <see cref="UnparameterizedDynamicSqlFindings"/> stream (docs/detection-checklist.md
    /// Tier 2 "Dynamic SQL quality": concatenated value in constant dynamic SQL, and
    /// EXEC(string) where sp_executesql with params was possible). Bumped to 25 for the new
    /// <see cref="NonPersistedComputedColumnFindings"/> stream (docs/detection-checklist.md
    /// "Schema-scan UDF and computed-column findings": non-persisted computed column,
    /// independent of whether it references a UDF). Bumped to 26 for the new
    /// <see cref="TempTableExecShapeFindings"/> stream (docs/detection-checklist.md Tier 2
    /// "Dynamic SQL quality" item 3: temp-table shape mismatch across a proc-call boundary,
    /// <c>INSERT INTO #temp EXEC OtherProc</c>). Live-mode only - always empty in a file-mode
    /// scan, exactly like every other stream whose verdict needs a real database round trip
    /// (<see cref="CrossTableTypeDriftFindings"/>'s FK-linked half, the indexed-view registry).
    /// Bumped to 27 for the new <see cref="SelfReferencingDmlFindings"/> stream
    /// (docs/detection-checklist.md Tier 2 "Halloween Protection and self-referencing DML").
    /// Bumped to 28 for the new <see cref="TemporalTableHistoryIndexGapFindings"/> stream
    /// (docs/detection-checklist.md "Temporal table history-side index gap"). Live-mode only -
    /// always empty in a file-mode scan, same reasoning as <see cref="CrossTableTypeDriftFindings"/>'s
    /// FK-linked half. Bumped to 29 for the new <see cref="ModuleCompileFlagFindings"/> stream
    /// (docs/detection-checklist.md "Small precise adds": WITH RECOMPILE, and a table-valued
    /// function's own un-COLLATE'd return-table string column). Live-mode only, same reasoning.
    /// Bumped to 30 for three new syntax-only streams (docs/detection-checklist.md "Small precise
    /// adds"): <see cref="WindowFrameFindings"/> (RANGE instead of ROWS in window-function
    /// frames), <see cref="WaitForFindings"/> (WAITFOR DELAY/TIME), and
    /// <see cref="ViewOrderingFindings"/> (TOP(100) PERCENT / ORDER BY in a view or inline TVF).
    /// Bumped to 31 for the new <see cref="TransactionHygieneFindings"/> stream (docs/detection-
    /// checklist.md "Small precise adds": the first half of the "Transaction hygiene pair" item -
    /// BEGIN TRANSACTION with no reachable COMMIT/ROLLBACK on some path). Bumped to 32 for the
    /// new <see cref="CompositeIndexLeadingColumnFindings"/> and <see cref="IndexHintFindings"/>
    /// streams (docs/detection-checklist.md "Hint and index-shape catalog checks"). Bumped to 33
    /// for five new streams from docs/detection-checklist.md "Second OSS/commercial sweep": <see
    /// cref="SessionDateSettingFindings"/> (SET DATEFORMAT/DATEFIRST mid-module), <see
    /// cref="CartesianJoinFindings"/> (true cartesian join), <see
    /// cref="UndersizedDeclarationFindings"/> (declared type of size 1 or 2), <see
    /// cref="TruncateSwallowedFindings"/> (TRUNCATE inside a TRY whose CATCH swallows the error),
    /// and <see cref="UnindexedTempTableUsageFindings"/> (SELECT INTO temp table later joined/
    /// filtered with no index). Bumped to 34 for the new <see cref="OutputParameterFindings"/>
    /// stream (docs/detection-checklist.md "Second OSS/commercial sweep": "Output parameter not
    /// populated on every code path" - a path-sensitive reachability rule, shipped standalone
    /// rather than folded into the Tier 4 "output parameter never assigned" entry, since Tier 4
    /// itself stayed out of scope for this pass). Bumped to 35 for the new
    /// <see cref="DatabaseConfigurationFindings"/> stream (docs/detection-checklist.md "Second
    /// OSS/commercial sweep": "Database-level configuration flags" - PAGE_VERIFY, AUTO_SHRINK,
    /// AUTO_CLOSE, TARGET_RECOVERY_TIME, and Query Store state/capture mode). Live-mode only -
    /// always empty in a file-mode scan (there is no file-mode equivalent of "the database's own
    /// current configuration"), same reasoning as <see cref="TempTableExecShapeFindings"/>.
    /// Bumped to 36 for the new <see cref="ParameterReassignmentPredicateFindings"/> stream
    /// (docs/detection-checklist.md "Catch-all / kitchen-sink predicates" sibling: "parameter
    /// overwritten before use in a predicate" - a formal parameter reassigned on every path
    /// reaching a later predicate use of the same name, defeating the compile-time sniffed value).
    /// Bumped to 37 for the new <see cref="CodeMetricFindings"/> stream (docs/detection-
    /// checklist.md Tier 4 "Size and complexity metrics" - eight configurable-threshold
    /// structural metrics: line length, module length, routine length, parameter count, nesting
    /// depth, conditional-operator count, CASE branch count, CASE branch length).
    /// Bumped to 38 for the new <see cref="FormattingFindings"/> stream (docs/detection-
    /// checklist.md Tier 4 "Formatting and layout": tab characters, multiple statements/
    /// declarations sharing a line, missing/single-line-unbraced conditional bodies, a statement
    /// visually dangling off an unbraced conditional, an IF sharing a line with a prior block's
    /// own END, redundant parentheses, and a module with no leading comment).
    /// Bumped to 39 for the new <see cref="NamingFindings"/> stream (docs/detection-checklist.md
    /// Tier 4 "Naming and identifiers" plus the standalone "redundant database/schema qualifier"
    /// bullet: a reserved keyword used as a table/column/index/routine identifier, a user-defined
    /// procedure or function named with the "sp_" prefix, a CREATE with no explicit schema
    /// qualifier, and a redundant "dbo." qualifier on a user-defined type reference).
    /// Bumped to 40 for the new <see cref="DeadCodeFindings"/> stream (docs/detection-checklist.md
    /// Tier 4 "Dead and duplicated code" - the five members needing real control-flow/dataflow
    /// analysis: unreachable code, an unused label, an unused local variable, an unused
    /// non-OUTPUT parameter, and a GOTO whose target is the very next statement).
    /// Bumped to 41 for the new <see cref="DuplicationFindings"/> stream (docs/detection-checklist.md
    /// Tier 4 "Dead and duplicated code" - the pattern-matching half: commented-out code, a
    /// duplicated string literal, a WHILE loop that can only run once, a self-assignment, identical
    /// operands either side of a comparison/logical/self-referential-arithmetic operator, a
    /// repeated unary operator, and a negated comparison written as the negation of its opposite).
    /// Bumped to 42 for eight new <see cref="Predicates.DuplicationFindingKind"/> members closing
    /// out the rest of the same Tier 4 "Dead and duplicated code" bullet (conditional-structure
    /// comparisons): a duplicated sibling IF/CASE condition, identical/all-identical branch
    /// bodies, a redundant or mutually-exclusive AND-combined numeric bound, a collapsible nested
    /// IF, a nested IIF, and an always-true/always-false literal-vs-literal comparison - no new
    /// finding record or list, but a consumer enumerating this existing field's possible string
    /// values deserves the same signal any other new content gets, matching the
    /// <see cref="Predicates.FindingConfidence"/> field's own precedent above.
    /// </summary>
    /// Bumped to 43 for the new <see cref="DeprecatedSyntaxFindings"/> stream (docs/detection-
    /// checklist.md Tier 4 "Task-comment tracking" and "Non-ANSI and deprecated spellings"): TODO/
    /// FIXME comments, non-ANSI comparison operators, the "= NULL"/"&lt;&gt; NULL" silent
    /// always-false trap, a wildcard-free LIKE pattern, a legacy system compatibility view, a table
    /// hint without WITH, a numbered-procedure-group definition/invocation, a string-literal column
    /// alias, a removed legacy security stored procedure, and SET ROWCOUNT.
    /// Bumped to 44 for the new <see cref="StatementShapeFindings"/> stream (docs/detection-
    /// checklist.md Tier 4 "Statement-shape advice"): INSERT without a column list, ordinal
    /// ORDER BY, TOP without ORDER BY, a table with no primary key, a routine missing SET NOCOUNT
    /// ON, and a bare SELECT *.
    public const int CurrentSchemaVersion = 44;
}
