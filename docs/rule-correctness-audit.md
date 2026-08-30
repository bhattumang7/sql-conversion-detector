# Rule correctness audit

Ongoing pass auditing each shipped rule scanner's own logic against real SQL
Server engine behaviour (oracle-verified against a live instance where
possible, `vendor/sql2025` reference source otherwise) — not missing
detections (`detection-tasklist.md` covers those), but places where a
scanner's own reported verdict diverges from what the real engine actually
does: a false positive, a false negative, or a materially wrong claim in a
finding's own message. Confirmed items are recorded here for a later fix
pass and are not fixed inline as part of this audit.

An item only lands here once it is confirmed against the real engine or an
authoritative source, not merely suspected — this project treats a
false-positive bug report the same way it treats a false-positive finding:
worse than not reporting it at all.

---

## Confirmed bugs (open)

- [ ] **`IndexDesignScanner.VariableLengthKeyColumnExceedsKeyLimit` only
      checks one variable-length key column at a time, never the composite
      sum, and ignores fixed-length columns in that sum entirely.**
      (`src/SilentScan.Core/Predicates/IndexDesignScanner.cs`,
      `CheckVariableLengthKeyColumnWidth`). Oracle-confirmed (SQL Server
      2025): the engine's 900/1700-byte key-length ceiling applies to the
      combined width of every key column in the index, fixed-length columns
      included — two `VARCHAR(500)` key columns (1000 bytes combined) or an
      `INT` plus a `VARCHAR(898)` key column (902 bytes combined) both print
      the engine's deferred-failure warning, even though neither individual
      column exceeds the limit on its own and the scanner currently reports
      nothing for either shape. Separately confirmed: whenever the
      fixed-length-only portion of a key already exceeds the limit by
      itself (one wide `CHAR`, or several `CHAR` columns summing past it),
      `CREATE INDEX` fails immediately (Msg 1944) rather than warning — loud,
      not silent, so that shape should stay out of scope, matching the
      scanner's existing single-fixed-column exclusion.

---

## Audited, no bug found

- `WriteLossClassifier` (`Rules/WriteLossClassifier.cs`) — variable-target-only
  scoping for `LengthTruncation` is intentional (table-column narrowing is a
  hard compile error, not a silent loss); re-verified live.
- `AnsiNullDfltFlowResolver` + `CatalogBuilder`'s ANSI_NULL_DFLT fallback —
  re-verified the ON/OFF and OFF/OFF no-op asymmetry against a live instance;
  matches the already-shipped logic from the prior fix.
- `SqlTypeCategory` enum ordering used by `ExpressionTypeInferencer.Combine`
  for CASE/IIF branch-type merging — matches Microsoft's documented data-type
  precedence table exactly, member-by-member.
- A handful of hardcoded builtin-function return lengths (`SUSER_SNAME`,
  `USER_NAME`, `APP_NAME`, `DB_NAME`, `HOST_NAME` = nvarchar(128);
  `ORIGINAL_LOGIN` = nvarchar(4000)) — confirmed via
  `sys.dm_exec_describe_first_result_set`.

---

## Not yet audited

AggregateDivisionColumnstore, AlterColumnSafety, AlwaysEncryptedKeyColumn,
AlwaysEncryptedOrderBy, BareTopNoOrderBy, CartesianJoin,
CascadingForeignKey, CatchAllPredicate, CheckConstraint, CodeMetric,
ColumnCollationDrift, ColumnstoreUnsupportedColumnType,
CompositeIndexLeadingColumn, ControlFlowRisk, CrossModuleLockOrder,
CrossTableTypeDrift, DeadCode, DefaultNullableConstraint, DeprecatedSyntax,
DmlTargetTable, Duplication, FloatEqualityPredicate,
FloatOrderDependentAggregate, ForcedParameterization, ForcedSerial,
Formatting, IdentityRange, IndexCoverage, IndexHint, MaxTypedColumn,
MemoryOptimizedForeignKey, MemoryOptimizedUnsupportedColumnType,
MemoryOptimizedUnsupportedIndexOption, MissingStatistics,
ModuleCompileFlag, MultiReferencedCte, Naming, NestedViewDepth,
NonPersistedComputedColumn, NonSargablePredicate, NonUniqueUpdateSource,
NotInNullableSubquery, OperandComparability, OutputParameter,
ParameterReassignmentPredicate, PartialCompositeForeignKeyJoin,
PostExpansionJoinWidth, ProcCallArgumentMismatch, QueryAntiPattern,
ScalarUdf, SchemaDependency, Security, SecurityPredicateIndex,
SelectiveXmlIndexValueColumn, SelectStarView, SelfReferencingDml,
SessionDateSetting, SetOption, SpExecuteSqlParameterMismatch,
StaleSelectStarView, StatementShape, StringConcatNull,
TemporalTableHistoryIndexGap, TempTableExecShapeCandidate,
TransactionHygiene, TriggerCorrectness, TriggerOrder,
TriggerRecursionCycle, TruncateSwallowed, TryCastComputedColumnPredicate,
TvfFence, UnindexedTempTableUsage, UntrustedConstraint, ViewOrdering,
WaitFor, WindowFrame, WindowFunctionArgument
