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

- [ ] **`AlterColumnSafetyScanner` never detects `FLOAT(n)` precision
      narrowing — the declared bit-precision is dropped during type
      resolution, before the scanner ever sees it.**
      (`src/SilentScan.Core/Parsing/SqlTypeReferenceResolver.cs:45` builds
      `Float`/`Real` types via the catch-all `_ => new SqlType(category.Value)`,
      discarding the declared parameter; `Rules/NumericFamilyNarrowing.cs:32`
      then hardcodes the approximate-family rank to `53` regardless of the
      actual declared value, so `FLOAT(53)` and `FLOAT(24)` are
      indistinguishable to the classifier). Oracle-confirmed (SQL Server
      2025): `ALTER TABLE ... ALTER COLUMN V FLOAT(24)` on a column holding
      `1.23456789012345` silently rounds it to `1.2345679` with no error —
      exactly the silent precision loss `AlterColumnSafetyScanner` exists to
      catch — but it never fires for any `FLOAT(n<=24)` narrowing target.

- [ ] **`AlterColumnSafetyScanner` misses combined `DECIMAL`/`NUMERIC`
      precision+scale narrowing when both facets nominally widen but the
      integer-digit budget (precision − scale) shrinks.**
      (`Rules/NumericFamilyNarrowing.cs`: `IsDecimalPrecisionNarrowed`
      compares `Precision` head-to-head and the family rank compares only
      `Scale` — neither computes `Precision - Scale`, the actual whole-number
      capacity that determines overflow.) Oracle-confirmed (SQL Server 2025):
      `ALTER TABLE ... ALTER COLUMN V DECIMAL(12,6)` on a `DECIMAL(10,2)`
      column holding `12345678.12` raises `Msg 8115, Arithmetic overflow
      error converting numeric to data type numeric` — both precision (10→12)
      and scale (2→6) individually increase, but integer-digit capacity
      shrinks 8→6, and the scanner reports nothing because each individual
      facet looks like a widening.

- [ ] **`AlwaysEncryptedOrderByScanner` never detects an ordinal-position
      `ORDER BY` referencing an Always Encrypted column.**
      (`src/SilentScan.Core/Predicates/AlwaysEncryptedOrderByScanner.cs:47`
      requires `element.Expression is ColumnReferenceExpression`; `ORDER BY
      <ordinal>` parses to an integer literal expression, not a column
      reference, so it's silently skipped before the encryption-type check
      ever runs.) Oracle-confirmed (SQL Server 2025): `SELECT Id, Ssn FROM
      dbo.T ORDER BY 2` against a deterministic/randomized-encrypted `Ssn` in
      the 2nd select-list position raises the same `Msg 33277, Encryption
      scheme mismatch` the rule is designed to catch — but the scanner
      produces no finding for the ordinal form, only the by-name form.

- [ ] **`BareTopNoOrderByScanner`'s "100 percent" carve-out only matches an
      integer literal, likely missing the decimal form of the same
      value.** (`src/SilentScan.Core/Predicates/BareTopNoOrderByScanner.cs:42-43`,
      `IsHundredPercent` matches only `IntegerLiteral { Value: "100" }`.)
      Oracle-confirmed (SQL Server 2025): `TOP 100.0 PERCENT` is valid T-SQL
      and returns every row, the same semantically-100%-equivalent case the
      rule's own doc explicitly carves out for the plain `100` form. A
      decimal literal like `100.0`/`100.00` tokenizes to a `NumericLiteral`,
      not an `IntegerLiteral`, in ScriptDom's standard literal
      classification (digits containing a decimal point are never an
      integer literal) — the same literal-type distinction this codebase
      already relies on elsewhere (e.g. `WriteLossClassifier.IsWithinScaleLiteral`).
      That would make `IsHundredPercent` return `false` for `TOP 100.0
      PERCENT`, producing a false positive on a query that is not actually
      unstable-order-risk. Flagged with slightly lower confidence than the
      others above: the engine-equivalence half is live-oracle-confirmed,
      but the ScriptDom literal-type half was reasoned from the parser's
      well-established tokenization rules and this codebase's own existing
      usage, not from re-running the built scanner against the input.

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
- `AggregateDivisionColumnstoreScanner` — message is explicitly framed as an
  unproven structural heuristic (`FindingConfidence.Low`, no hard engine
  claim to falsify); detection logic matches that deliberately loose scope.
- `AlwaysEncryptedKeyColumnScanner` — the `EncryptionType: Randomized,
  EnclaveSupport: Disabled` gate matches real engine behavior for all three
  key-column kinds (index, constraint, statistics); additionally
  live-verified the `CREATE STATISTICS` path directly (raises Msg 33573,
  matching the index/constraint paths already covered by existing oracle
  tests).
- `CartesianJoinScanner` — purely structural claim (no oracle-verifiable
  engine-error text to check); traced the connectivity/union-find logic
  through third-table transitivity, self-references, parenthesized/negated
  predicates, `CROSS APPLY` exclusion, and the conservative bail-out on any
  unqualified column reference — all consistent with the existing test
  suite and the project's deliberately false-negative-favoring design here.

---

## Not yet audited

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
