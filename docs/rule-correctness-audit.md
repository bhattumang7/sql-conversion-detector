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

- [ ] **`CheckConstraintScanner`'s identity-column finding overclaims
      "failures silently stop forever" for any CHECK on an identity column,
      but that's only true for a monotonic one-sided threshold.**
      (`src/SilentScan.Core/Predicates/CheckConstraintScanner.cs:74` fires
      for any CHECK constraint referencing an identity column, regardless of
      predicate shape; the shipped message —
      `RuleCatalog.cs:129`/`Sarif/SarifReportWriter.cs:790-791` — says every
      failing insert "fails deterministically (Msg 547)... until the counter
      catches up and failures silently stop forever" for every case it
      fires on.) Oracle-confirmed (SQL Server 2025): a periodic predicate
      like `CHECK (Id % 2 = 0)` on an identity column alternates
      succeed/fail forever and never "stops" — inserting 4 rows in sequence
      against `IDENTITY(1,1)` fails on odd values (Msg 547) and succeeds on
      even ones, indefinitely. A reverse-direction threshold like `CHECK (Id
      < 1000)` is the mirror-image divergence: satisfied at first, then
      *permanently* failing once the counter passes 1000 — the opposite of
      "stops mattering." The message's narrative only actually holds for a
      one-sided `col > N`/`col >= N` shape, which the scanner's trigger
      condition doesn't require.

- [ ] **`ColumnstoreUnsupportedColumnTypeScanner` only ever flags
      `SQL_VARIANT`; the real columnstore column-type gate is materially
      broader and, for MAX-length string/binary types, depends on clustered
      vs. nonclustered — a distinction the scanner already has available but
      doesn't use for type-gating.**
      (`src/SilentScan.Core/Predicates/ColumnstoreUnsupportedColumnTypeScanner.cs:14-16`
      — the only type check is `Category: SqlTypeCategory.SqlVariant`.)
      Oracle-confirmed (SQL Server 2025), same Msg 35343 ("has a data type
      that cannot participate in a columnstore index") the shipped rule is
      built around: `xml`, `hierarchyid`, `geometry`, `geography`, `ntext`,
      `text`, `image`, and `rowversion` columns are all rejected on both
      clustered and nonclustered columnstore indexes, the same as
      `sql_variant`. Separately, `varchar(max)`/`nvarchar(max)`/
      `varbinary(max)` are rejected on a NONCLUSTERED columnstore index but
      explicitly *allowed* on a CLUSTERED columnstore index on the same
      table/column — a real clustered/nonclustered split in the engine's own
      gate that a single unconditional type check can't express. (Already
      flagged in general terms by `docs/detection-tasklist.md`; this entry
      adds the concrete confirmed type list and the clustered/nonclustered
      MAX-type split.)

- [ ] **`ControlFlowRiskScanner`'s `TriggerEmitsOutputRuleId` unconditionally
      claims a trigger's SELECT/PRINT "sends output back to whatever
      connection fired the DML," but under the server-level `disallow
      results from triggers` setting a trigger SELECT hard-fails the
      triggering DML instead of silently forwarding anything.**
      (`src/SilentScan.Core/Predicates/ControlFlowRiskScanner.cs:111-133`
      fires unconditionally for a real SELECT/PRINT in a trigger body;
      `RuleCatalog.cs:251` and
      `Reporting/RuleDocs/ControlFlow/TriggerEmitsOutput.cs:11-19` state the
      "sends output back" framing with no caveat.) Oracle-confirmed (SQL
      Server 2025): with `sp_configure 'disallow results from triggers'` set
      to `1` (a real, documented, off-by-default server option), a trigger
      body's `SELECT 1 AS x` against a fired `INSERT` raises `Msg 524, A
      trigger returned a resultset and the server option 'disallow results
      from triggers' is true` instead of returning anything to the caller —
      a genuine server-setting-dependent behavior split the rule's message
      doesn't acknowledge. (Whether the same setting affects `PRINT` the
      same way as `SELECT` was not directly tested — Msg 524's own wording
      says "resultset" specifically, so this is noted but not asserted.)

- [ ] **`CrossModuleLockOrderScanner` records a procedure's write order
      across its whole body instead of per explicit transaction, so two
      writes that were never lock-held concurrently can be reported as a
      lock-order disagreement.**
      (`src/SilentScan.Core/Predicates/CrossModuleLockOrderScanner.cs:169-203`
      — `_writes` is reset only in `EnterProcedure`, never at `BEGIN
      TRANSACTION`, so `RecordWrite`'s dedup-by-first-occurrence spans
      transactions that already committed and released their locks before a
      later, separate transaction re-touches the same table.) Oracle-confirmed
      (SQL Server 2025, via `sys.dm_tran_locks`): a lock on a table is
      released at `COMMIT` and not re-acquired until that table is next
      touched. For a procedure shaped `BEGIN TRAN; UPDATE T1; COMMIT; BEGIN
      TRAN; UPDATE T2; UPDATE T1; COMMIT;`, the scanner records the
      procedure's order as `[T1, T2]` (from T1's first, already-committed
      appearance) even though the only transaction that ever holds both
      locks simultaneously acquires them `T2` then `T1` — the opposite of
      what the scanner reports, and identical to a sibling procedure that
      does `BEGIN TRAN; UPDATE T2; UPDATE T1; COMMIT;`. The scanner flags
      these two procedures as disagreeing on lock order (deadlock risk) when
      no deadlock cycle can actually form between them. False positive.

- [ ] **`DeprecatedSyntaxScanner`'s legacy-compatibility-view name list
      includes `syslocks`, which is not and never was a real SQL Server
      compatibility view.**
      (`src/SilentScan.Core/Predicates/DeprecatedSyntaxScanner.cs:12-20`,
      `LegacyCompatibilityViewNames`.) Oracle-confirmed (SQL Server 2025):
      `SELECT OBJECT_ID('sys.syslocks')` returns `NULL` and `SELECT * FROM
      sys.syslocks` raises `Msg 208, Invalid object name 'sys.syslocks'` —
      the real backward-compatibility view for lock info is `syslockinfo`,
      already separately and correctly present in the same list. Every one
      of the other 34 names in the list was cross-checked against
      `sys.all_objects` and does exist as a real `is_ms_shipped` compatibility
      view; `syslocks` is the only one that doesn't. If a scanned codebase
      has an ordinary object literally named `syslocks`, the scanner falsely
      claims it "is a pre-SQL-Server-2005 system compatibility view."

- [ ] **`FloatEqualityPredicateScanner`'s published rule doc claims `<>`
      coverage the scanner never implements.**
      (`src/SilentScan.Core/Predicates/FloatEqualityPredicateScanner.cs:81-89`
      only matches `BooleanComparisonType.Equals`, never
      `NotEqualToBrackets`/`NotEqualToExclamation`; but
      `src/SilentScan.Core/Reporting/RuleDocs/Predicates/FloatEquality.cs:22`
      says "A WHERE or ON predicate using `=` (or `<>`) against a
      FLOAT/REAL value...". `RuleCatalog.cs:61`'s own SARIF message correctly
      says only "(=)" — so the RuleCatalog and the RuleDoc disagree with each
      other, and the shipped code matches RuleCatalog, not the published
      doc.) A `WHERE FloatCol <> 0.3` predicate against a float/real column
      is silently never flagged despite the published doc explicitly
      claiming it would be.

- [ ] **`ForcedParameterizationScanner` has two finding kinds that falsely
      claim their literal argument stays unparameterized under
      `PARAMETERIZATION FORCED`, when the real engine parameterizes both.**
      (`src/SilentScan.Core/Predicates/ForcedParameterizationScanner.cs:119-138`,
      `CheckSumArgumentLiteral` and `DoubleColonCallArgumentLiteral`;
      messages at `RuleCatalog.cs:287,291`.) Oracle-confirmed (SQL Server
      2022 and 2025, `ALTER DATABASE ... SET PARAMETERIZATION FORCED`, cached
      plan text inspection): `WHERE Val > 22 AND CHECKSUM('LitArgX') = 0`
      compiles to `... and CHECKSUM ( @1 ) = @2` — the CHECKSUM literal
      argument is parameterized, not left literal. Likewise `WHERE Val > 55
      AND geography::Parse('POINT(1 1)').STAsText() = 'x'` compiles to `...
      and geography :: Parse ( @1 ) . STAsText ( ) = @2` — the static-call
      literal argument is also parameterized. Both finding kinds are false
      positives with a factually wrong message claim; all other kinds in
      this family (LIKE pattern, TOP/OFFSET-FETCH, SELECT-list, HAVING,
      ORDER BY/GROUP BY expression, TABLESAMPLE size, DML OUTPUT list,
      CONVERT style code, constant-foldable expression) were independently
      re-verified correct via matched-pair cached-plan probes.

- [ ] **`IdentityRangeScanner` crashes (unhandled `OverflowException`)
      instead of reporting for `DECIMAL`/`NUMERIC` IDENTITY columns with
      precision 29-38, a legal and reachable range.**
      (`src/SilentScan.Core/Predicates/IdentityRangeScanner.cs:86-95`,
      `DecimalMax` computes `10^precision - 1` by repeated `decimal`
      multiplication; `decimal.MaxValue` has 29 significant digits and C#
      `decimal` arithmetic is always overflow-checked, so precision ≥ 29
      throws instead of returning a value.) Oracle-confirmed (SQL Server
      2025): `CREATE TABLE ... (Id DECIMAL(38,0) IDENTITY(1,1) ...)` —
      `IDENTITY` on `DECIMAL(38,0)`, the maximum legal precision, is valid
      and commonly reachable, and the scanner's own `TypeBound` switch
      already claims to handle `SqlTypeCategory.Decimal` — so this is a
      real crash on a legal schema across roughly a quarter of the type's
      legal precision range, not a theoretical input.

- [ ] **`IndexCoverageScanner`'s clustering-key fallback picks any
      `PRIMARY KEY`-kind index without checking it's actually the table's
      clustering key, so a table with a `NONCLUSTERED PRIMARY KEY` can miss
      a real Key/RID Lookup.**
      (`src/SilentScan.Core/Predicates/IndexCoverageScanner.cs:80-83`: the
      second fallback branch selects
      `table.Indexes.FirstOrDefault(i => i.Kind == CatalogIndexKind.PrimaryKey)`
      without an `IsClustered` check, then unions those columns into the
      "already covered by every nonclustered index" set — but only a real
      clustering key or heap RID is actually carried into a nonclustered
      index's leaf rows, not a nonclustered primary key's own columns.)
      Oracle-confirmed (SQL Server 2025) via plan XML: a heap table with
      `CONSTRAINT PK_HeapT PRIMARY KEY NONCLUSTERED (Id)` and a separate
      nonclustered index on `A` — `SELECT Id, A FROM ... WHERE A = 5`
      forced onto the `A` index produces `PhysicalOp="RID Lookup"` in the
      real plan, because that index's leaf rows carry only the heap's RID,
      not `Id`. Tracing the scanner's own logic against this shape: the
      incorrect fallback resolves `clusteringKeyColumns` to `[Id]`, marks
      `Id` as covered, and the scanner reports no finding at all for a
      query that provably needs a lookup — a false negative directly
      contradicting the rule's own "oracle-confirmed via real plan XML"
      claim. `CatalogIndex.IsClustered` is already populated correctly from
      `sys.indexes.type_desc` in the tool's live-catalog path, so this is a
      genuine logic bug reachable through the shipped tool, not a
      data-population artifact.

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
- `CascadingForeignKeyScanner` — fires on any FK action other than
  `NO ACTION` (CASCADE/SET NULL/SET DEFAULT); message and rule doc already
  hedge with "or nulls, or resets" rather than overclaiming "cascade" for
  the non-CASCADE actions. Purely catalog-derived, no session/DB-setting
  dependency to diverge on.
- `ColumnCollationDriftScanner` — baseline resolution (database default vs.
  tempdb-effective collation for temp objects/table variables) matches
  existing scanner tests; message is already hedged ("risks a
  collation-conflict compile error or a forced-scan implicit conversion")
  and carries only `FindingConfidence.Medium`, consistent with its
  heuristic scope.
- `CompositeIndexLeadingColumnScanner` — the "cannot be seek-used at all
  without a bound leading column" claim holds on SQL Server 2025, including
  with an explicit `WITH (INDEX(...))` hint forcing the index; verified no
  newer "index skip scan" feature invalidates it (still a full `Index
  Scan`, never a seek, when only the non-leading column is bound).
- `CrossTableTypeDriftScanner` — the differing-category-or-collation gate
  matches SQL Server's documented data-type precedence table exactly; any
  two differing in-model categories genuinely sit at different precedence,
  so a join always converts the lower-precedence side, matching the rule's
  "one side always loses seek" claim.
- `DeadCodeScanner` — the `ReachabilityWalker` control-flow model correctly
  treats RETURN/THROW as terminal, requires every IF/TRY-CATCH branch to be
  terminal (a `THROW` inside `TRY` alone doesn't make the block terminal —
  it's ANDed with `CATCH`'s own terminality), and never treats `WHILE` as
  terminal; unused-variable/parameter logic correctly separates reads from
  writes. Pure static-AST rule, no real-engine fact to diverge from beyond
  the definitional "RETURN/THROW end the routine."
- `DefaultNullableConstraintScanner` — only fires when a `DEFAULT`-bearing
  column is still nullable, matching the uncontested fact that a `DEFAULT`
  only applies when a column is omitted from an INSERT's column list, and
  an explicit `NULL` always overrides it.
- `DmlTargetTable` (helper feeding `IndexDesignScanner`'s
  `ColumnstoreIndexOnDmlTargetTable` rule, no own finding/rule id) — wired
  correctly, not dead code; `DmlWriteTargetResolver` correctly excludes CTE
  self-references and only resolves catalog-confirmed real tables
  (synonyms resolved through), matching its "direct DML target" framing —
  writes through an updatable view are intentionally out of scope, same as
  sibling rules.
- `FloatOrderDependentAggregateScanner` — aggregate-name gate (SUM/AVG/VAR/
  VARP/STDEV/STDEVP only, MIN/MAX/COUNT excluded) matches the rule doc's
  explicit claim; `OverClause is null` deliberately excludes windowed
  aggregates, a documented scope boundary rather than a divergence;
  `BaseColumnResolver` only resolving direct column-reference arguments
  (not an expression like `Value * 2`) is a precision-first scope limit,
  not a false claim.
- `ForcedSerialScanner` — all three `NonParallelPlanReason` claims
  oracle-verified via real plan XML (table-variable INSERT target →
  no-parallel-nested-transaction; `OBJECT_ID`/`@@TRANCOUNT`/
  `IDENT_CURRENT`/`ERROR_MESSAGE` inside a query with FROM →
  nonparallelizable intrinsic; FAST_FORWARD/bare FORWARD_ONLY READ_ONLY
  cursors → no-parallel cursor), including the negative cases
  (`@@ROWCOUNT`, `SCOPE_IDENTITY()`, `LOCAL STATIC FORWARD_ONLY READ_ONLY`,
  a no-option cursor, a `DYNAMIC` cursor) correctly never firing.

---

## Not yet audited

CatchAllPredicate, CodeMetric, Duplication,
Formatting, IndexHint, MaxTypedColumn,
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
