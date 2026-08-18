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
    IReadOnlyList<FilteredIndexParameterMismatchFinding> FilteredIndexParameterMismatchFindings,
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
    IReadOnlyList<ControlFlowRiskFinding> ControlFlowRiskFindings,
    IReadOnlyList<SecurityFinding> SecurityFindings,
    IReadOnlyList<IndexDesignFinding> IndexDesignFindings,
    IReadOnlyList<IdentityRangeFinding> IdentityRangeFindings,
    IReadOnlyList<FloatEqualityFinding> FloatEqualityFindings,
    IReadOnlyList<QueryAntiPatternFinding> QueryAntiPatternFindings,
    IReadOnlyList<IndexCoverageFinding> IndexCoverageFindings,
    IReadOnlyList<TriggerCorrectnessFinding> TriggerCorrectnessFindings,
    IReadOnlyList<CrossModuleLockOrderFinding> CrossModuleLockOrderFindings,
    IReadOnlyList<TriggerRecursionCycleFinding> TriggerRecursionCycleFindings,
    IReadOnlyList<CheckConstraintFinding> CheckConstraintFindings,
    IReadOnlyList<DefaultNullableConstraintFinding> DefaultNullableConstraintFindings,
    IReadOnlyList<TryCastComputedColumnPredicateFinding> TryCastComputedColumnPredicateFindings,
    IReadOnlyList<StaleSelectStarViewFinding> StaleSelectStarViewFindings,
    IReadOnlyList<BareTopNoOrderByFinding> BareTopNoOrderByFindings,
    IReadOnlyList<StringConcatNullFinding> StringConcatNullFindings,
    IReadOnlyList<AggregateDivisionColumnstoreFinding> AggregateDivisionColumnstoreFindings,
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
    /// Bumped to 45 for the new <see cref="ControlFlowRiskFindings"/> stream (docs/detection-
    /// checklist.md Tier 4 "Cursor and control-flow correctness"): a cursor FETCH whose INTO list
    /// doesn't match its own defining SELECT's column count, an empty CATCH block, output emitted
    /// from a trigger, a dirty-read isolation hint, duplicated call arguments, and @@IDENTITY.
    /// Bumped to 46 for the new <see cref="SecurityFindings"/> stream (docs/detection-checklist.md
    /// Tier 4 "Security"): a hard-coded credential-suggestive variable assigned a literal, a
    /// hard-coded IP address, a HASHBYTES call naming a weak/deprecated algorithm (general use and
    /// sensitive-context use), and a dynamic SQL call site this tool cannot prove is free of
    /// runtime/external influence.
    /// Bumped to 47 for the new <see cref="IndexDesignFindings"/> stream (docs/detection-
    /// checklist.md "DBA-script family sweep (2026-08-17)" §A "Physical/schema design", the
    /// clustered/nonclustered-flag-dependent group): heap with nonclustered indexes present, heap
    /// with a nonclustered primary key, non-unique clustered index, wide clustered key, and a
    /// uniqueidentifier clustered key defaulted to NEWID(). Live-mode only - always empty in a
    /// file-mode scan, same reasoning as <see cref="TempTableExecShapeFindings"/>/<see
    /// cref="DatabaseConfigurationFindings"/> (the new <see cref="Catalog.CatalogIndex.IsClustered"/>
    /// flag it depends on is populated only by a live catalog read).
    /// Bumped to 48 for the rest of docs/detection-checklist.md "DBA-script family sweep
    /// (2026-08-17)" §A: nine more <see cref="IndexDesignFindings"/> kinds (duplicate/prefix-
    /// subsumed indexes, unindexed foreign keys, disabled/hypothetical indexes, over-indexing -
    /// many nonclustered indexes on one table and any single index with too many key columns -
    /// and three low-confidence table-shape signals: wide table, high nullable-column ratio, high
    /// string-column ratio), and three more <see cref="DatabaseConfigurationFindings"/> kinds
    /// (auto-create/auto-update statistics off, compatibility level behind the connected engine
    /// instance's own current default). Both streams stay live-mode only, same reasoning as the
    /// bump to 47.
    /// Bumped to 49 for the remaining four docs/detection-checklist.md "DBA-script family sweep
    /// (2026-08-17)" §A items: four more <see cref="IndexDesignFindings"/> kinds (a filtered
    /// index's own filter columns absent from its key/INCLUDE list, deprecated LOB column types -
    /// text/ntext/image - and the naming-only timestamp/rowversion recommendation, and float/real
    /// used as an index key column), plus two new streams of their own - <see
    /// cref="IdentityRangeFindings"/> (identity seed/increment anomaly - schema-decidable - and
    /// identity range near-exhaustion - data-state-decidable, meaningful only against a
    /// production-shaped target) and <see cref="FloatEqualityFindings"/> (an AST-level equality
    /// predicate against a float/real column - a correctness claim, not a sargability one).
    /// <see cref="IndexDesignFindings"/>/<see cref="IdentityRangeFindings"/> stay live-mode only,
    /// same reasoning as the bump to 47/48; <see cref="FloatEqualityFindings"/> is a genuine
    /// AST+catalog pass that runs in both file and live mode, same as every ordinary per-module
    /// stream.
    /// Bumped to 51 for the new <see cref="QueryAntiPatternFindings"/> stream (docs/detection-
    /// checklist.md "DBA-script family sweep (2026-08-17)" §B "Query anti-patterns still
    /// unbuilt"): a table variable used as a query source under a connected compatibility level
    /// below 150 (oracle-confirmed fixed 1-row estimate) or inside a WHILE loop that also writes
    /// it (oracle-confirmed stale estimate frozen at the first iteration's size); a WHILE loop
    /// issuing single-row UPDATE/DELETE keyed to its own per-iteration tracked variable (RBAR); a
    /// cursor declared without LOCAL; a local variable assigned COUNT(*) then compared only to
    /// zero in the very next statement (oracle-confirmed to force a real full-set aggregation,
    /// unlike the inline scalar-subquery form, which the optimizer already rewrites into an
    /// EXISTS-equivalent semi-join and this stream deliberately never flags); a HAVING condition
    /// referencing only GROUP BY key columns/literals; a UNION of branches provably disjoint by a
    /// same-column distinct-literal equality; and a SELECT DISTINCT joining a table whose own
    /// join columns aren't backed by a unique index. The first kind is live-mode only (needs the
    /// new <see cref="Catalog.DatabaseCatalog.CompatibilityLevel"/>, itself live-mode only); every
    /// other kind runs in both file and live mode.
    /// Bumped to 52 for the rest of docs/detection-checklist.md "DBA-script family sweep
    /// (2026-08-17)" §B: seven more <see cref="QueryAntiPatternFindings"/> kinds (an unqualified
    /// table reference at a real query site; three MERGE hazards - missing HOLDLOCK/SERIALIZABLE,
    /// a non-unique USING source oracle-confirmed to hard-error the statement, and an unconditional
    /// WHEN MATCHED/WHEN NOT MATCHED BY SOURCE THEN DELETE branch; a recursive CTE with no
    /// OPTION (MAXRECURSION n), oracle-confirmed the engine's own 100-level default fails the
    /// statement with Msg 530; a whole-table UPDATE/DELETE with no WHERE and no TOP; and a linked-
    /// server 4-part name or live-confirmed cross-database 3-part reference), plus a new <see
    /// cref="IndexCoverageFindings"/> stream (a WHERE-equality seek against a table's own single
    /// candidate nonclustered index that does not cover every other column the statement references
    /// on that table - oracle-confirmed via real plan XML that the non-covering shape produces a
    /// <c>Lookup="1"</c> Key/RID lookup and the covering shape does not). Every kind in both new
    /// streams runs in both file and live mode; <see cref="Predicates.QueryAntiPatternFindingKind.
    /// LinkedServerOrCrossDatabaseReference"/>'s own cross-database (not linked-server) half is
    /// live-mode only, needing the already-existing <see cref="Catalog.DatabaseCatalog.
    /// CurrentDatabaseName"/>.
    /// Bumped to 53 for docs/detection-checklist.md "DBA-script family sweep (2026-08-17)" §C
    /// "Trigger correctness" and §D "Cross-module analysis", closing out the entire sweep: the new
    /// <see cref="TriggerCorrectnessFindings"/> stream (a variable assigned from a single,
    /// unspecified row of inserted/deleted with no WHERE/TOP/aggregate - oracle-confirmed to
    /// silently bind an arbitrary row's value when the trigger's own multi-row DML fires it more
    /// than once - plus the sharper sub-kind where that variable then drives a keyed UPDATE/DELETE
    /// straight-line in the same trigger body; a trigger with no IF NOT EXISTS/@@ROWCOUNT-style
    /// early-out guard, genuinely low-confidence and advisory; and a trigger that writes directly
    /// back to its own target table, oracle-confirmed to actually re-fire - not silently no-op -
    /// only when the connected database's own RECURSIVE_TRIGGERS option is live-confirmed on, via
    /// the new <see cref="Catalog.DatabaseCatalog.IsRecursiveTriggersEnabled"/>, live-mode only)
    /// and the new <see cref="CrossModuleLockOrderFindings"/> stream (two top-level procedures'
    /// own direct explicit-transaction write orders disagreeing on the relative lock order of the
    /// same two base tables - the textbook deadlock shape - deliberately scoped to direct bodies
    /// only, not the full call-graph-transitive version the checklist first sketched; see that
    /// finding's own doc comment for the honest scope-down). <see
    /// cref="TriggerCorrectnessFindings"/>'s <c>DirectRecursiveTrigger</c> kind is live-mode only;
    /// every other kind in both new streams runs in both file and live mode, matching <see
    /// cref="QueryAntiPatternFindings"/>'s own precedent for a mixed-mode stream. "SET NOCOUNT ON
    /// missing from a trigger" was already covered by the existing <see
    /// cref="StatementShapeFindingKind.MissingSetNocountOn"/> kind - not duplicated, only
    /// strengthened here by adding the <c>CREATE OR ALTER TRIGGER</c>/<c>CREATE OR ALTER
    /// PROCEDURE</c> forms <see cref="StatementShapeScanner"/> had never visited (a real, if
    /// narrow, pre-existing coverage gap found while verifying that claim, not part of the sweep
    /// itself).
    /// Bumped to 54 for docs/detection-checklist.md full-archive and second full-archive
    /// practitioner sweeps §E/§G's index/catalog-design items: the new
    /// <see cref="FilteredIndexParameterMismatchFindings"/> stream (a filtered index's own
    /// literal-equality filter, oracle-confirmed unmatchable by a real query-site predicate that
    /// filters the same column via a parameter/variable instead of the literal - a structural
    /// compile-time optimizer limitation, not fixed by RECOMPILE); four new
    /// <see cref="Predicates.IndexDesignFindingKind"/> members
    /// (<see cref="Predicates.IndexDesignFindingKind.VariableLengthKeyColumnExceedsKeyLimit"/>,
    /// scoped to variable-length types only after oracle verification showed the checklist's
    /// original "CREATE INDEX hard-fails" premise was wrong for them - true only for fixed-length
    /// types, which are already-excluded hard-DDL-error territory;
    /// <see cref="Predicates.IndexDesignFindingKind.MergeableIndexesDifferingIncludeOnly"/>;
    /// <see cref="Predicates.IndexDesignFindingKind.ColumnstoreIndexOnDmlTargetTable"/> and
    /// <see cref="Predicates.IndexDesignFindingKind.MonotonicClusteredKeyMissingSequentialOptimization"/>,
    /// both structural risk flags only); and two new additive <see cref="Catalog.CatalogIndex"/>
    /// fields those last two kinds and the merge-candidate kind need
    /// (<see cref="Catalog.CatalogIndex.KeyColumnIsDescending"/>,
    /// <see cref="Catalog.CatalogIndex.OptimizeForSequentialKey"/>).
    /// Bumped to 55 for the new <see cref="TriggerRecursionCycleFindings"/> stream
    /// (docs/detection-checklist.md "Second full-archive practitioner sweep (2026-08-18)" §G
    /// "Multi-hop trigger recursion cycle across tables") - the scanner and finding type existed
    /// already but were never actually wired into this report; this bump is that wiring landing.
    /// Bumped to 56 for the new <see cref="CheckConstraintFindings"/> stream (docs/detection-
    /// checklist.md Tier 2 §A: "CHECK constraint that doesn't account for NULL" and "CHECK
    /// constraint accidentally placed on an IDENTITY column") - both oracle-confirmed against the
    /// standing Docker instance, catalog-plus-text-decidable from the newly additive
    /// <see cref="Catalog.CatalogCheckConstraint.DefinitionText"/> field.
    /// Bumped to 57 for three new streams from docs/detection-checklist.md "Second full-archive
    /// practitioner sweep" §G: <see cref="DefaultNullableConstraintFindings"/> (a DEFAULT
    /// constraint on a column that is still nullable - a caller supplying an explicit NULL
    /// silently bypasses the default, oracle-confirmed, no query text needed at all - runs in both
    /// file and live mode); <see cref="TryCastComputedColumnPredicateFindings"/> (a non-persisted
    /// computed column built on TRY_CAST, oracle-confirmed non-deterministic and therefore never
    /// indexable, referenced in a real filter-context predicate elsewhere in the corpus - also runs
    /// in both file and live mode); and <see cref="StaleSelectStarViewFindings"/> (a SELECT * view
    /// over a single base table whose own frozen compiled column list has drifted from that base
    /// table's current shape - oracle-confirmed to silently surface real data under a stale,
    /// wrong column label, not merely a missing/extra column - live-mode only, needing the new
    /// <see cref="Catalog.DatabaseCatalog.TryGetViewCompiledColumns"/> registry).
    /// Bumped to 58 for three new streams from docs/detection-checklist.md "Second full-archive
    /// practitioner sweep" §G: <see cref="BareTopNoOrderByFindings"/> (a bare <c>TOP (n)</c> with no
    /// <c>ORDER BY</c> anywhere in the query - the returned row set is not guaranteed deterministic
    /// per SQL Server's own documented absence of a guarantee; runs in both file and live mode,
    /// pure AST, no catalog needed); <see cref="StringConcatNullFindings"/> (the <c>+</c> operator
    /// silently propagating a single NULL operand to NULL for a whole concatenated string,
    /// oracle-confirmed against <c>CONCAT()</c>'s different, non-nulling behavior; runs in both
    /// modes); and <see cref="AggregateDivisionColumnstoreFindings"/> (a CASE-guarded division
    /// inside an aggregate argument on a table carrying a columnstore index - shipped as a
    /// structural risk flag only, Low confidence, after a real but unsuccessful live-reproduction
    /// attempt against this environment's own engine build; runs in both modes, using the same
    /// <see cref="Catalog.CatalogIndex.IsColumnstore"/> flag already populated by both the file-mode
    /// and live-mode catalog builders).
    public const int CurrentSchemaVersion = 58;
}
