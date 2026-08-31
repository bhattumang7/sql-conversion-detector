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

First full pass: all 78 rule scanner families audited, 35 confirmed
correctness bugs found; 34 remain open below.

---

## Confirmed bugs (open)

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

- [ ] **`DuplicationScanner`'s `IdenticalBinaryOperands` finding treats any
      two textually-identical non-column expressions as provably equal,
      with no non-determinism gate — so a repeated non-deterministic
      function call is claimed to be "always the same value, a tautology,
      or a fixed degenerate result" when it demonstrably isn't.**
      (`src/SilentScan.Core/Predicates/DuplicationScanner.cs`:
      `CanClaimTautologyOrContradiction` (~line 282-290) returns `true` for
      any non-`ColumnReferenceExpression`, with no determinism check, gating
      the `Equals`-comparison path; the `AND`/`OR` path (~line 206-213) and
      the `Subtract`/`Divide`/`Modulo` self-reference path (~line 215-224)
      fire on same-text operands with no gate at all.) Oracle-confirmed (SQL
      Server 2025): `NEWID() = NEWID()` across 7.6M cross-joined rows
      matched zero times — never true, refuting "always the same value /
      tautology" for the equality path; `RAND() - RAND()` across multiple
      rows produced a fixed *non-zero* value every time (two textually
      identical `RAND()` calls in one statement evaluate to two different
      numbers), refuting "fixed degenerate result" (implying zero) for the
      self-subtraction path. `WHERE NEWID() = NEWID()` and expressions like
      `RAND() - RAND()` would both be flagged with `FindingConfidence.High`
      asserting claims that are empirically false for non-deterministic
      functions (`NEWID`, `RAND`, `NEWSEQUENTIALID`, `CHECKSUM`, etc.).

- [ ] **`NamingScanner`'s `SpPrefixOnUserRoutine` reverses the real
      name-resolution order — it claims `master` is checked before the
      caller's own database, but the engine checks the current database
      first and only falls back to `master` when no local match exists.**
      (`src/SilentScan.Core/Predicates/NamingScanner.cs:156-162`; claim text
      in `RuleCatalog.cs:208` and
      `Reporting/RuleDocs/Naming/SpPrefixOnUserRoutine.cs:12-23` — "SQL
      Server searches the master database first for any unqualified
      call.") Oracle-confirmed (SQL Server 2025 and 2022, both directions,
      creation-order swapped to rule out cache/creation-order artifacts): a
      user database's own `sp_`-prefixed routine always wins over a
      same-named `master` procedure when both exist — an unqualified call
      resolves to the local copy, not `master`. `master` only gets used
      when the local routine is absent. The described danger direction is
      backwards: the real risk is a local `sp_`-prefixed routine silently
      shadowing a same-named system procedure (or falling through to
      `master` only when the local one is missing), not `master` winning
      outright.

- [ ] **`NamingScanner`'s `RedundantTypeQualifier` claims a `dbo.` schema
      qualifier on a user-defined type "adds nothing" unconditionally, but
      the same default-schema dependency the rule's own doc cites for other
      schemas also applies to `dbo`.**
      (`src/SilentScan.Core/Predicates/NamingScanner.cs:190-206` special-cases
      `dbo` via `Common/SchemaObjectNameHelper.cs:7`; claim text in
      `RuleCatalog.cs:210` and
      `Reporting/RuleDocs/Naming/RedundantTypeQualifier.cs:10-25` asserts
      `dbo.MyType` and `MyType` "resolve to the exact same object" for "the
      overwhelming majority of databases.") Oracle-confirmed (SQL Server
      2025): an unqualified user-defined type reference resolves via the
      connecting principal's own default schema first, exactly like an
      unqualified table/object name — not unconditionally to `dbo`. With
      both `dbo.mytype` (`FROM int`) and `alt.mytype` (`FROM varchar(50)`)
      defined in the same database, and a principal whose
      `DEFAULT_SCHEMA = alt`, `CREATE TABLE ... (col mytype)` run under that
      principal binds the column to `alt.mytype`, not `dbo.mytype`. The
      rule's own stated mechanism for why other schemas are risky to
      de-qualify applies equally to `dbo` whenever a same-named type exists
      in another schema and the DDL later runs under a principal whose
      default schema is that other one — stripping the qualifier per this
      rule's own fix guidance can silently change which type gets bound.

- [ ] **`NonPersistedComputedColumnScanner` claims a non-persisted computed
      column "recalculates its own expression from the base row every
      single time a query touches it," unconditionally — false whenever the
      column is covered by an index.**
      (`src/SilentScan.Core/Predicates/NonPersistedComputedColumnScanner.cs:17-22`
      fires for `IsComputed && !IsPersisted` with no indexing check;
      `RuleCatalog.cs:145` and
      `Reporting/RuleDocs/Catalog/NonPersistedComputedColumn.cs:10-28`
      explicitly frame the "recomputes on every read" claim as
      "definitionally true... not something that needs confirming against a
      real engine," regardless of indexing.) Oracle-confirmed (SQL Server
      2025): a non-persisted computed column (`Sum AS (A + B)`, no
      `PERSISTED`) covered by a nonclustered index (`CREATE INDEX ... ON
      T1(Sum)`) — `SELECT Sum FROM T1 WHERE Sum = 12345` plans to an `Index
      Seek` on that index, with the plan's `Compute Scalar` operator doing a
      pure pass-through of the already-materialized `Sum` column, not an
      `A+B` re-evaluation. The index itself stores the computed value; no
      base-row recompute happens for reads served from it. The finding's own
      premise that no oracle confirmation is needed is the bug — the claim
      is only true in the unindexed case.

- [ ] **`NonSargablePredicateScanner`'s computed-column match for
      `YEAR`/`MONTH`/`DAY` predicates compares the canonicalized `DATEPART`
      unit argument with plain case-insensitive string equality, missing
      T-SQL's standard datepart synonym spellings — so an indexed computed
      column the real optimizer actually uses via an index seek still gets
      reported as a forced scan.**
      (`src/SilentScan.Core/Predicates/ComputedColumnMatcher.cs:59-104`,
      `StructurallyEqual`/`TryAsCanonicalDatePart` — canonicalizes
      `YEAR(x)`/`MONTH(x)`/`DAY(x)` to `DATEPART(<func-name>, x)` but then
      compares the datepart-unit identifier literally, e.g. `"YEAR"` against
      a computed column defined with `DATEPART(yy, ...)`, without
      normalizing synonym spellings like `yy`/`yyyy`/`year`.)
      Oracle-confirmed (SQL Server 2025): a computed column defined as
      `DATEPART(yy, Col) PERSISTED` and indexed is matched by the real
      optimizer to both `WHERE YEAR(Col) = 2024` and `WHERE DATEPART(year,
      Col) = 2024` — both produce `Index Seek` in the plan. Because the
      scanner's synonym comparison fails (`"YEAR"` ≠ `"yy"` as plain
      strings), `HasIndexedMatchingComputedColumn` returns `false` and the
      scanner reports `DateFunctionOnColumn`/non-sargable ("forcing a
      scan") for a predicate the engine actually seeks on. False positive.
      (Verified as correct in the same pass, not a bug: `ISNULL` suppression
      on a known-not-null indexed column; `col + 0`/`col * 1`/`col - 0`
      still forcing a scan, not simplified away by the optimizer;
      `UPPER`/`LOWER` on a case-insensitive-collation indexed column still
      forcing a scan.)

- [ ] **`OperandComparabilityScanner`'s comparability gate never checks the
      native `json` type, so a `json`-typed operand in a comparison/IN/
      BETWEEN/NULLIF/ORDER BY/GROUP BY/DISTINCT position is silently never
      flagged.**
      (`src/SilentScan.Core/Predicates/OperandComparabilityScanner.cs:176-181`,
      `TryClassify`'s switch matches `Xml` and
      `Text`/`NText`/`Image` but has no `SqlTypeCategory.Json` case, falling
      through to `null`.) Oracle-confirmed (SQL Server 2025): `json = json`
      raises `Msg 13636, The JSON data type cannot be compared or sorted,
      except when using the IS NULL operator` — the identical restriction
      shape as `xml`'s `Msg 305`, and this codebase's own sibling
      `VerdictClassifier.IsOutOfModelCategory` already buckets `Json`
      together with `Xml`/`Text`/`NText`/`Image` for exactly this reason.
      False negative squarely inside the rule's own stated scope.

- [ ] **`OperandComparabilityScanner`'s `IN` handling only inspects the
      tested (left-hand) expression, never the value list, so an XML/legacy-
      large-object value appearing as a member of the `IN (...)` list is
      never flagged.**
      (`src/SilentScan.Core/Predicates/OperandComparabilityScanner.cs:89-92`
      calls `InspectMembership(inPredicate.Expression, ...)` and never
      inspects `InPredicate.Values`.) Oracle-confirmed (SQL Server 2025):
      `WHERE Name IN (XmlColumn)` (an ordinary `varchar` column being
      compared against an `xml` value inside the list) raises `Msg 402, The
      data types varchar and xml are incompatible in the equal to operator`
      — exactly the "used in an IN list" shape `RuleCatalog.cs:69-70`
      already claims to cover — but goes unflagged because only the tested
      `varchar` expression is ever classified, not the `xml` member of the
      list.

- [ ] **`OutputParameterScanner` treats `SELECT @p = col FROM t WHERE ...`
      (a non-aggregate variable-assignment SELECT) as an unconditional
      write, when it silently does nothing if the WHERE clause matches zero
      rows — so a procedure whose only assignment to an OUTPUT parameter
      takes this form can leave the caller's variable completely unchanged
      without the scanner ever reporting it.**
      (`src/SilentScan.Core/Predicates/VariableWriteSites.cs:15-21` yields a
      write for any `SelectSetVariable` in a `QuerySpecification`'s select
      list, with no check for whether the query is guaranteed to produce a
      row; this feeds `OutputParameterScanner.Rule.PerStatement`
      (`OutputParameterScanner.cs:97-105`), which removes the parameter from
      the unassigned set on any such statement.) Oracle-confirmed (SQL
      Server 2025): `DECLARE @x INT = 42; SELECT @x = Val FROM #T WHERE Id =
      1;` against a table with no matching row leaves `@x` at `42`,
      completely unchanged — while the aggregate form, `SELECT @y =
      SUM(Val) FROM #T2 WHERE Id = 1` under the identical zero-row
      condition, does assign (`@y` becomes `NULL`), confirming aggregate
      vs. non-aggregate is exactly the line that matters and the scanner
      doesn't draw it. This is precisely the "caller's own variable is left
      completely unchanged" scenario `RuleCatalog.cs:169` says the rule is
      built to catch, but it produces no finding here. Not exercised by the
      existing test suite, which only covers the always-executes,
      no-`FROM`-clause form (`SELECT @x = 1;`).

- [ ] **`QueryAntiPatternScanner`'s `AlterTableSwitchColumnMismatch` shape
      check omits collation, missing a real, distinct engine error that's
      squarely inside the rule's own stated scope.**
      (`src/SilentScan.Core/Predicates/QueryAntiPatternScanner.cs:397-398`,
      `HasSameShape` compares `Category`/`Length`/`Precision`/`Scale`/
      `IsMax` but never `SqlType.Collation`, even though `Collation` is
      already populated on both sides.) Oracle-confirmed (SQL Server 2025):
      a partitioned target column declared `VARCHAR(10) COLLATE
      Latin1_General_CI_AS` against a source staging column declared
      `VARCHAR(10) COLLATE SQL_Latin1_General_CP1_CI_AS` — identical in
      every field `HasSameShape` checks — raises `Msg 4945, ALTER TABLE
      SWITCH statement failed because column 'Col' does not have the same
      collation` on `ALTER TABLE ... SWITCH TO ... PARTITION`, a distinct
      error from the Msg 4944 type/length/precision/scale mismatch this
      rule already documents. Neither `RuleCatalog.cs` nor the published
      rule doc discloses collation as excluded, and the rule's own
      column-pair walk already exists for exactly this purpose — false
      negative. (IDENTITY mismatch was separately confirmed correct as a
      non-blocker, so the gap is specifically collation.)

- [ ] **`SchemaDependencyScanner` never detects a scalar UDF referenced
      inside a table-level `DEFAULT` constraint added via `ALTER TABLE ...
      ADD CONSTRAINT df DEFAULT dbo.fn() FOR col`, despite that shape being
      squarely inside the rule's own documented scope.**
      (`src/SilentScan.Core/Catalog/SchemaExpressionCollector.cs:38-50`,
      `CollectCheckConstraints` only matches `CheckConstraintDefinition` in
      a table's `TableConstraints` list; a table-level
      `DefaultConstraintDefinition` lands in the exact same list for this
      syntax form and is silently skipped — `DefaultConstraintDefinition`
      isn't referenced anywhere else in `Catalog/`.) Confirmed the syntax is
      real, standard, and works today (SQL Server 2025): `ALTER TABLE dbo.T
      ADD CONSTRAINT DF_Col DEFAULT dbo.fn_Stamp() FOR Col` succeeds and the
      function runs on every insert that omits the column. The equivalent
      inline column-level form (`Col INT DEFAULT dbo.fn()` inside `CREATE
      TABLE`) is correctly caught via a separate code path — the gap is
      specific to the table-level/`ALTER TABLE ADD CONSTRAINT` form, which
      `RuleCatalog.cs:49`'s "A computed column, DEFAULT, or CHECK constraint
      definition calls a scalar UDF" claim already covers in principle.

- [ ] **`SpExecuteSqlParameterMismatchScanner` never records a parameter
      binding for a positional `sp_executesql` call — only named
      (`@Param = value`) syntax is recognized — so the rule silently never
      fires for one of the two standard `sp_executesql` calling
      conventions, regardless of any real type-narrowing mismatch.**
      (`src/SilentScan.Core/Predicates/ProcCallGraphBuilder.cs:210-229`,
      `TryRecordSpExecuteSqlParameterBindings`: the binding loop only
      matches when `actual.Variable is { } namedFormal` and looks it up by
      name; ScriptDom's `ExecuteParameter.Variable` is null for a plain
      positional actual argument, so a positional call produces zero
      bindings.) Oracle-confirmed (SQL Server 2025): `EXEC sp_executesql
      @sql, N'@SkuCode VARCHAR(10), @out VARCHAR(20) OUTPUT', @sku,
      @result OUTPUT` — with `@sku` declared `VARCHAR(20)` holding a
      22-character value — silently truncates to 10 characters
      (`@result = 'WIDGET-202'`, `LEN = 10`), the exact narrowing scenario
      the rule's own doc example describes, reproduced with positional
      instead of named syntax. The scanner's own regular-EXEC call-folding
      path in the same file already handles positional arguments correctly
      (`MatchFoldedArguments`), so this is specifically an
      `sp_executesql`-binding-path omission, not a parser limitation.
      Positional `sp_executesql` calls are a real, commonly used calling
      convention (this repo's own test fixtures for other scanners use it),
      not a contrived edge case, and every existing test for this
      scanner/binder exclusively uses named syntax, so the gap was never
      exercised.

- [ ] **`StatementShapeScanner`'s `TableWithNoPrimaryKey` claims "no
      engine-enforced row uniqueness" for any table lacking a `PRIMARY KEY`,
      even when the table carries a `UNIQUE` constraint/index that enforces
      exactly that.**
      (`src/SilentScan.Core/Predicates/StatementShapeScanner.cs:43-56` only
      suppresses the finding when
      `table.Indexes.Any(i => i.Kind == CatalogIndexKind.PrimaryKey)`,
      ignoring the separately tracked `CatalogIndexKind.UniqueConstraint`;
      message: "no engine-enforced row uniqueness"; doc adds "nothing stops
      two rows from being byte-for-byte identical".) Oracle-confirmed (SQL
      Server 2025): a table with `Id INT NOT NULL UNIQUE` and no primary
      key rejects a duplicate `Id` insert with `Violation of UNIQUE KEY
      constraint` — demonstrably engine-enforced row uniqueness, directly
      contradicting the message. (The doc's separate, narrower claim that
      change tracking specifically requires a real `PRIMARY KEY` — not
      satisfied by `UNIQUE` — was independently oracle-confirmed correct;
      only the "no engine-enforced row uniqueness" phrasing is false.)

- [ ] **`StringConcatNullScanner` never tracks `SET
      CONCAT_NULL_YIELDS_NULL`, and the rule's own doc falsely claims
      NULL-propagating `+` is "the only behavior at all in recent
      compatibility levels."**
      (`Reporting/RuleDocs/Predicate/StringConcatNull.cs:12-14`; the
      scanner's `Rule` class in
      `src/SilentScan.Core/Predicates/StringConcatNullScanner.cs` has no
      `SET`-statement handler at all, unlike sibling scanners in this
      codebase that do track other SET options.) Oracle-confirmed (SQL
      Server 2025, `compatibility_level = 170`, the newest level): `SET
      CONCAT_NULL_YIELDS_NULL OFF; SELECT 'a' + NULL` returns `'a'`, not
      `NULL` — both statements run without error at the newest compat
      level, so the setting is not forced/locked as the doc claims. A
      module with this SET active would have every `+`-NULL-propagation
      finding reported against behavior that doesn't actually occur.

- [ ] **`TriggerCorrectnessScanner`'s `InsteadOfInsertFilteredNoRejectPath`
      false-positives when an `INSTEAD OF INSERT` trigger routes rows into
      two or more mutually-exclusive filtered `INSERT`s — no rows are
      actually dropped, but every one of those statements gets flagged as
      silently dropping rows.**
      (`src/SilentScan.Core/Predicates/TriggerCorrectnessScanner.cs:228-265`,
      line 249: `hasCompanionInsert` only recognizes an *unconditional*
      extra `INSERT` as a companion/catch-all, never checking whether two or
      more filtered inserts' predicates jointly cover the input — so a
      trigger where every `INSERT` is itself filtered always has
      `hasCompanionInsert = false` regardless of coverage.) Oracle-confirmed
      (SQL Server 2025): an `INSTEAD OF INSERT` trigger with `INSERT INTO
      Included ... WHERE Val > 0` and `INSERT INTO Excluded ... WHERE Val <=
      0` — a common "route rows to different tables by filter" pattern —
      accounts for every inserted row across the two tables with zero rows
      lost. Both statements would be flagged, each falsely claiming "rows
      matching the negated filter are silently dropped, no error, no
      trace."

- [ ] **`TempTableExecShapeCandidateScanner`'s `ColumnCountMismatch` ignores
      an explicit column list on `INSERT INTO #temp (<cols>) EXEC proc`,
      comparing the executed proc's described column count against the temp
      table's *full* declared column count instead — so a narrower explicit
      column list produces a false "always raises a hard runtime error"
      claim.**
      (`src/SilentScan.Core/Predicates/TempTableExecShapeCandidateScanner.cs:25-52`
      never reads `InsertSpecification.Columns`, always using the temp
      table's complete catalog-declared columns;
      `src/SilentScan.Live/Catalog/TempTableExecShapeChecker.cs:83-92`
      compares that full count against the proc's described result-set
      column count with no way to know a partial list was present;
      `RuleCatalog.cs:180` claims "this always raises a hard runtime error
      (Msg 213/8164) every time the statement executes.") Oracle-confirmed
      (SQL Server 2025): `#Results` declared with 3 columns
      (`Col1`/`Col2`/`Col3 DEFAULT 99`), executed proc's real result set has
      2 columns — `INSERT INTO #Results (Col1, Col2) EXEC proc` runs with
      **no error**, `Col3` taking its default. The scanner, comparing 3
      declared columns against 2 described columns while ignoring the
      explicit `(Col1, Col2)` list, would report a guaranteed failure that
      provably does not occur. Not covered by the existing test suite,
      which has no case with an explicit column list narrower than the temp
      table's full column set.

- [ ] **`TruncateSwallowedScanner` fires a duplicate finding for a nested
      TRY/CATCH around a swallowed `TRUNCATE`, one copy of which
      misattributes the swallow to an outer CATCH block that never actually
      runs for that error.**
      (`src/SilentScan.Core/Predicates/TruncateSwallowedScanner.cs:34-48`,
      `OnEnterTryCatchStatement` runs a full-subtree search for the
      `TRUNCATE` and for a propagating statement independently for *every*
      `TryCatchStatement` node, nested ones included, since the visitor
      recurses into every descendant unconditionally.) For `BEGIN TRY BEGIN
      TRY TRUNCATE TABLE dbo.Foo; END TRY BEGIN CATCH END CATCH; END TRY
      BEGIN CATCH END CATCH;`, T-SQL routes the error to the *nearest*
      enclosing CATCH — the inner one — so the inner `TryCatchStatement`
      finding is correct, but the outer node is also visited: its
      full-subtree search finds the same `TRUNCATE` (seeing through the
      inner TRY) and its own empty CATCH, producing a second, identical
      finding at the same source location whose implicit claim ("this
      block's CATCH lets the failure continue silently") is false — the
      outer CATCH never executes for this error at all. Not covered by the
      existing test suite, which only has flat, single-level TRY/CATCH
      cases.

- [ ] **`TryCastComputedColumnPredicateScanner` false-negatives for any
      database reporting a compatibility level below 110 (including
      currently-supported level 100), because it re-parses the computed
      column's definition text using a parser grammar that has no
      production for `TRY_CAST` syntax at all — even though `TRY_CAST`
      itself is not actually gated by compatibility level on the real
      engine.**
      (`src/SilentScan.Core/Predicates/TryCastComputedColumnPredicateScanner.cs:41-52`,
      `DefinesTryCast` calls `SqlScriptParser.ParseText(...,
      compatibilityLevel: catalog.CompatibilityLevel)`;
      `src/SilentScan.Core/Parsing/SqlScriptParser.cs:81-93` maps any
      `compatibilityLevel < 110` to `TSql100Parser`, whose generated grammar
      (confirmed against the exact ScriptDom package version this project
      references) has zero references to `TryCastCall` anywhere, unlike
      `TSql110Parser` and later.) Oracle-confirmed (SQL Server 2025, `SET
      COMPATIBILITY_LEVEL = 100`): `TRY_CAST(...)` in a computed column
      definition deploys and evaluates identically regardless of database
      compatibility level — it is not a compat-level-gated feature at all.
      For a database at compat < 110, the scanner's own re-parse of the
      column's `TRY_CAST(...)` text fails to parse, `DefinesTryCast`
      returns `false`, and the column is silently dropped as a candidate —
      even though the real engine treats it exactly as the rule's own
      rationale describes (session-`DATEFORMAT`-dependent, non-persistable,
      non-indexable). Purely an internal parser-selection mistake:
      compatibility level is not a valid proxy for which functions the
      engine actually accepts.

- [ ] **`UnindexedTempTableUsageScanner` never flags a temp table joined via
      an old-style comma join or an explicit `CROSS JOIN`, even though that
      shape is squarely inside the rule's own stated scope ("used later as
      a JOIN operand").**
      (`src/SilentScan.Core/Predicates/UnindexedTempTableUsageScanner.cs:82-86`,
      `OnEnterJoinSearchCondition` only fires for `QualifiedJoin`
      (ANSI-92 `INNER/LEFT/RIGHT/FULL ... ON`) nodes; the `FilteredInWhere`
      path at lines 88-96 only matches a single-element
      `FromClause.TableReferences` list.) For `FROM #t, dbo.Other WHERE
      #t.Id = Other.Id` or `FROM #t CROSS JOIN dbo.Other WHERE #t.Id =
      Other.Id`, ScriptDom represents the FROM clause as a 2-element
      `TableReferences` list with no wrapping `QualifiedJoin` node at all —
      an AST shape this same codebase's own `CartesianJoinScanner`
      explicitly detects and handles as a distinct join-shape family,
      confirming it's real and reachable. Neither of `UnindexedTempTableUsageScanner`'s
      two match patterns covers it, so a `#temp` table joined this way and
      filtered by a WHERE-based join predicate is never reported at all —
      false negative for a real, still-valid, semantically identical join
      shape. Not covered by the existing test suite.

- [ ] **`WindowFrameScanner` reports `ImplicitDefaultRangeFrame` for
      ranking, offset, and distribution window functions
      (`ROW_NUMBER`/`RANK`/`DENSE_RANK`/`NTILE`/`LAG`/`LEAD`/
      `PERCENT_RANK`/`CUME_DIST`) that don't support a window frame at
      all — a fabricated finding on one of the most common window-function
      patterns in real T-SQL code.**
      (`src/SilentScan.Core/Predicates/WindowFrameScanner.cs:35-51`,
      `OnEnterOverClause` fires for any `OverClause` with an `ORDER BY` and
      no explicit frame clause, with no check for which function the
      `OVER` belongs to.) Oracle-confirmed (SQL Server 2025): attaching an
      explicit frame to any of `RANK`, `ROW_NUMBER`, `DENSE_RANK`, `NTILE`,
      `LAG`, `PERCENT_RANK`, `CUME_DIST` is a hard compile error, `Msg
      10752, The function '<name>' may not have a window frame` — proving
      no frame, implicit or explicit, is ever computed for them, unlike a
      true window-aggregate function (`FIRST_VALUE` with an explicit frame
      executed successfully in the same test). The claim itself —
      "T-SQL silently defaults this to RANGE BETWEEN UNBOUNDED PRECEDING
      AND CURRENT ROW" with "the exact same measured cost" as an explicit
      RANGE frame — is false for every one of these functions, since no
      frame concept applies to them. The shipped test
      `RowNumberWithOrderByNoFrame_FiresAsImplicitDefaultRange` locks in
      exactly this wrong behavior.

- [ ] **`WaitForScanner` resets its open-transaction tracker at every batch
      boundary (`GO`), so a `WAITFOR` in a later batch after a `BEGIN
      TRANSACTION` in an earlier batch of the same script is never
      recognized as being inside a transaction.**
      (`src/SilentScan.Core/Predicates/WaitForScanner.cs:36`,
      `OnEnterTSqlBatch` unconditionally zeroes `_openTransactionDepth` at
      the start of every `TSqlBatch`.) `GO` is purely a client-side batch
      separator — a transaction opened in one batch stays open into the
      next batch of the same session. Oracle-confirmed (SQL Server 2025):
      `BEGIN TRANSACTION; GO SELECT @@TRANCOUNT; WAITFOR DELAY '00:00:01';
      SELECT @@TRANCOUNT; ROLLBACK; GO` shows `@@TRANCOUNT = 1` on both
      sides of the `GO` and both sides of the `WAITFOR` — the transaction
      genuinely holds locks through the batch boundary and through the
      `WAITFOR`. The scanner reports `IsInsideTransaction = false` for this
      exact shape, missing precisely the more serious variant the rule's
      own doc calls out ("a WAITFOR inside a transaction extends that
      transaction's lock hold duration").

- [ ] **`ViewOrderingScanner` misses a top-level `UNION`/`EXCEPT`/
      `INTERSECT` query whose `ORDER BY`/`OFFSET...FETCH` sits directly on
      the set operation itself, rather than nested inside one branch.**
      (`src/SilentScan.Core/Predicates/ViewOrderingScanner.cs:90-96`,
      `OutermostQuerySpecification` only unwraps `QueryParenthesisExpression`
      and matches `QuerySpecification`; any other `QueryExpression` —
      including a `UNION`/`EXCEPT`/`INTERSECT` `BinaryQueryExpression` —
      falls through to `null`, and `Inspect` returns immediately without
      ever looking at that node's own `OrderByClause`/`OffsetClause`.) In
      ScriptDom, `OrderByClause`/`OffsetClause` are declared on the
      abstract `QueryExpression` base class itself, shared by both
      `QuerySpecification` and `BinaryQueryExpression` — a top-level `SELECT
      ... UNION ALL SELECT ... ORDER BY ... OFFSET ... FETCH ...` genuinely
      carries its own ORDER BY/OFFSET on the union node, not buried inside
      either branch. Oracle-confirmed this deploys and runs as valid view
      SQL on SQL Server 2025. False negative squarely inside the rule's own
      stated scope ("the view/inline TVF's own outermost query uses a
      genuinely row-limiting TOP(N) or OFFSET...FETCH together with ORDER
      BY") — distinct from the existing test's intentionally-declined case,
      where the ORDER BY is nested inside one UNION branch's own
      parentheses rather than attached to the union itself.

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
- `IndexHintScanner` — existing oracle tests already confirm Msg 308
  (nonexistent hinted index) and the seek→scan degradation for an unbound
  hinted-index leading column; independently confirmed the
  "referenced anywhere" column collector correctly resolves correlated
  references inside a subquery, so a leading-key column bound only in a
  correlated subquery doesn't cause a false positive.
- `MaxTypedColumnScanner` — live-verified the two differentiated claims:
  `VARCHAR(MAX)` is allowed as an INCLUDE column but rejected as a key
  column (Msg 1919), while legacy `TEXT`/`NTEXT`/`IMAGE` is rejected even
  as an INCLUDE column (Msg 1999) — matches the rule's two separate
  messages exactly.
- `MemoryOptimizedForeignKeyScanner` — cross-storage FK (Msg 10778) and
  non-`NO ACTION` referential actions between two memory-optimized tables
  (Msg 10794) match existing oracle coverage; additionally live-verified
  `ON DELETE SET NULL` (not just CASCADE) also fails the same way,
  confirming the scanner's blanket action check is correct. FK catalog data
  is read live from `sys.foreign_keys`, engine-authoritative by
  construction.
- `MemoryOptimizedUnsupportedColumnTypeScanner` — live-verified all six
  listed types (`xml`, `sql_variant`, `text`, `ntext`, `image`, `timestamp`)
  each fail with Msg 10794 on a memory-optimized table, matching the rule's
  claim verbatim; the rule text doesn't claim exhaustiveness, so absent
  spatial/CLR-UDT coverage is a scope gap, not a divergence.
- `MemoryOptimizedUnsupportedIndexOptionScanner` — the columnstore
  early-exit can't hide a real gap (nonclustered columnstore is flatly
  rejected on memory-optimized tables regardless of filter/include, and
  clustered columnstore syntactically can't carry INCLUDE/WHERE at all);
  clustered/included-column/filtered-index checks already oracle-tested
  (Msg 12317/10664/10794).
- `MissingStatisticsScanner` — auto-create-stats gate and
  leading-vs-non-leading statistic-column coverage logic both already
  oracle-tested end-to-end against a live catalog; the underlying catalog
  facts (`is_auto_create_stats_on`, `sys.stats`/`sys.stats_columns`) are
  read live, engine-authoritative by construction.
- `CatchAllPredicateScanner` — `(Column = @p OR @p IS NULL)` detection scope
  (equality-only, formal-parameter-only), the `WITH RECOMPILE`/
  `OPTION(RECOMPILE)` guard's exhaustiveness (confirmed all three
  CREATE/ALTER procedure statement forms share the same base type, and that
  triggers/functions cannot syntactically carry `WITH RECOMPILE` at all),
  and the dead-comparison absorption path are all correct or already
  oracle-tested.
- `ModuleCompileFlagScanner` — `RecompilesEveryCall` confirmed tied only to
  `WITH RECOMPILE` at create/alter time, unaffected by `sp_recompile`;
  `TableValuedFunctionReturnUsesDatabaseCollation` confirmed scoped exactly
  as documented across procedures/views/scalar functions/triggers/inline
  and multi-statement TVFs, including the schema-binding-sets-it-
  unconditionally case.
- `MultiReferencedCteScanner` — direct multi-reference already oracle-
  tested; newly verified transitive multi-reference (CTE B referencing CTE
  A twice, main body referencing only B once) via `STATISTICS IO` showing
  the base table scanned exactly the predicted number of times; recursive
  self-reference exclusion is structural; confirmed no CTE-shadowing path
  exists since T-SQL doesn't allow a `WITH` clause nested inside a
  subquery.

Skipped as pure style/structural, no real-engine claim to diverge from:
`CodeMetricScanner` (every finding kind's own text says "no query result or
plan is affected") and `FormattingScanner` (same framing; the one
underlying T-SQL fact — an unbraced `IF`/`WHILE` body is exactly one
statement — is uncontroversial syntax, not a claim needing verification).
- `NestedViewDepthScanner` — depth-accumulation arithmetic (`deepestChild +
  1`) matches the doc's own "2+ layers deep before reaching a base table"
  definition exactly; inline TVFs correctly folded into the same view map.
- `NonUniqueUpdateSourceScanner` — the uniqueness proof correctly requires
  the *entire* key-column set of a non-filtered, non-disabled unique index
  to be covered by the join's equality columns (a subset is correctly
  rejected); the `MERGE` Msg 8672 framing is already oracle-verified.
- `NotInNullableSubqueryScanner` — requires base-column provenance with no
  view indirection for the subquery's selected column, and only recognizes
  a top-level AND-flattened `IS NOT NULL` guard (an OR-nested one correctly
  does not suppress); the core three-valued-logic claim is already
  oracle-verified.
- `ParameterReassignmentPredicateScanner` — WITH RECOMPILE/OPTION(RECOMPILE)
  suppression at both proc and statement level, intersect-on-merge "every
  path" semantics, and the `Depth: 0` correlated-subquery guard are all
  correct. Note: this scanner shares `VariableWriteSites`
  (`Predicates/VariableWriteSites.cs`) with `OutputParameterScanner` above,
  so the same non-aggregate-`SELECT @p = col FROM t WHERE ...`-may-not-
  execute gap could in principle affect this rule's "reassigned before this
  predicate" claim in the opposite direction (false positive risk rather
  than false negative) — not independently oracle-demonstrated as a
  concrete divergence for this rule specifically, and the finding already
  defaults to `Confidence: Low`, so not filed as a second confirmed bug;
  worth re-checking once the shared primitive above is fixed.
- `PartialCompositeForeignKeyJoinScanner` — composite-only grouping,
  local-vs-statement-wide equality coverage split, comma-join dedup, and
  the unique-index suppression direction (`i.KeyColumns.All(usedColumns.Contains)`,
  confirmed as the mathematically correct direction) all check out against
  the existing test suite's JOIN/comma-join/UPDATE-FROM/CTE-shadowing
  coverage.
- `PostExpansionJoinWidthScanner` — the `MinimumGap = 3` threshold and
  unresolved/derived-table undercounting exactly match the doc's own
  "lower bound, never exhaustive" framing.
- `ProcCallArgumentMismatchScanner` — verified both directions:
  `WriteLossClassifier.Classify(target, source, ...)` called correctly as
  `(formal, caller)` for the in-direction and `(caller, formal)` for the
  writeback direction; oracle-confirmed an OUTPUT parameter receives the
  caller's pre-call value regardless of whether `OUTPUT` appears at the
  call site, so the unconditional in-direction check is correct, and the
  writeback direction is correctly gated on both the formal parameter being
  OUTPUT and the call site supplying the `OUTPUT` keyword.
- `QueryAntiPatternScanner` (all other finding kinds) — `ALTER TABLE SWITCH`
  index/constraint/filegroup/temporal/CDC/rule/full-text checks all
  internally consistent with their claimed error codes; grouping-sets
  cardinality limits oracle-verified at all three exact boundaries (CUBE
  12/13, ROLLUP 32/33, GROUPING SETS 4096/4097); `RecursiveCteMissingMaxRecursion`'s
  "default 100" claim is standard documented behavior.
- `ScalarUdfInlineabilityScanner` — `MinInliningCompatibilityLevel = 150`
  correct; `MaxInlineableTableReferenceCount = 49` independently
  oracle-verified via a plan-inlining sweep (49 scalar-subquery table
  references inlines, 50 doesn't) — exact match to the scanner's `> 49`
  gate.
- `SecurityScanner` — weak-hash-algorithm set (MD2/MD4/MD5/SHA/SHA1) matches
  the documented `HASHBYTES` deprecated-algorithm list; credential/IP
  heuristics carry no falsifiable engine-behavior claim.
- `SecurityPredicateIndexScanner` — leading-key-column-only match is
  explicitly documented as an intentional design choice (already
  oracle-tested scan-vs-seek claim, deliberately declines an unproven
  "forces serial execution" claim).
- `SelectiveXmlIndexValueColumnScanner` — both the 900-byte boundary (900
  OK, 901 fails Msg 6395) and the large-object case (Msg 6391)
  oracle-confirmed exact; separately confirmed this rule's `FOR (...)`
  clause is syntactically restricted to exactly one path on the real engine
  (a multi-path form is a parse error), so it does not share
  `IndexDesignScanner`'s composite-key-sum gap — checked and ruled out, not
  overlooked.
- `SelectStarViewScanner` — star-consumer exclusion, full-explicit-selection
  exclusion, multi-source unqualified-column decline, and CTE/derived-table
  non-attribution all match existing test coverage; a pure code-structure
  claim (frozen metadata), no engine-timing dependency to diverge on.
- `SelfReferencingDmlScanner` — `HasLiteralTopOne`'s PERCENT/variable/
  non-literal exclusions and the target-alias-skip logic across self-join
  aliases match already-oracle-confirmed Eager Spool/Distinct Sort presence
  and absence across INSERT/UPDATE/DELETE/MERGE, direct and through-view.
- `SessionDateSettingScanner` — deliberately coarse (any `SET
  DATEFORMAT`/`DATEFIRST` presence, no literal-pattern matching, `Low`
  confidence by design); both underlying claims (DATEFORMAT mdy/dmy
  resolving a literal differently, DATEFIRST 1/7 shifting `DATEPART(weekday,
  ...)`) verified live.
- `SetOptionScanner` — ARITHABORT's exclusion from `SyntaxOnlyTriggers` was
  already oracle-tested for filtered indexes; newly verified it also holds
  for indexed views (ARITHABORT made no difference to the NOEXPAND path,
  while ANSI_NULLS/QUOTED_IDENTIFIER OFF correctly triggered the documented
  silent view-expansion fallback).
- `StaleSelectStarViewScanner` — `FindSingleBaseTable`'s join/CTE-shadowing
  exclusion, real-table-only resolution, and the order-sensitive
  `SequenceEqual` column comparison (correct, since `SELECT *` is
  positional) all check out; the rule's motivating "not merely a
  missing/extra column" phrasing is contextual illustration, not
  contradicted by the scanner's own intentionally-broader trigger
  condition.
- `TemporalTableHistoryIndexGapScanner` — index-kind filtering and
  `SameKeyColumns`/`IsComparableIndex` logic match the existing test suite
  and the doc's stated criteria exactly; confirmed live that the engine
  itself refuses a plain `CREATE UNIQUE NONCLUSTERED INDEX` (not just a
  constraint) against a temporal history table, consistent with the
  scanner's uniqueness-agnostic comparison.
- `TransactionHygieneScanner` — the reachability walk's conditional-open/
  unconditional-close merge behavior was traced and doesn't produce a false
  claim under the rule's own stated scope (leaked transactions); Msg 266
  and `@@TRANCOUNT` behavior already oracle-tested. Three known blind spots
  are already tracked in `detection-tasklist.md` and excluded from this
  audit's scope, not re-verified here.
- `TriggerOrderScanner` — `is_first`/`is_last` grouping matches the real
  `sp_settriggerorder` invariant (at most one trigger can hold First, one
  Last, per table/event); the "≥2 unordered" threshold correctly treats a
  single remaining unpinned trigger as fully determined by elimination.
- `TriggerRecursionCycleScanner` — live-verified the
  `RECURSIVE_TRIGGERS`-vs-`nested triggers` distinction: `RECURSIVE_TRIGGERS
  OFF` does not stop an indirect cross-table trigger cascade (confirmed
  running unbounded), while the server-level `nested triggers` option gates
  cross-table cascading from the very first hop — the scanner correctly
  gates on `nested triggers` only and excludes the same-table 1-hop
  self-loop that `RECURSIVE_TRIGGERS` actually governs.
- `TvfFenceScanner` — `ClassifyDirectReference` correlated/standalone/
  from-or-join partitioning, the APPLY-only correlation gate, `InsertExec`
  matching, and inline-TVF resolution through the fence map all check out;
  existing oracle tests already verify `FromOrJoin`/`CorrelatedApply`/
  `InsertExec` outcomes against a real deployed engine.
- `UntrustedConstraintScanner` — `IsNotTrusted && !IsDisabled` filter
  matches live catalog semantics; confirmed disabling a trusted FK sets
  both `is_disabled` and `is_not_trusted`, so excluding disabled
  constraints is correct (the optimizer ignores them regardless of trust);
  the FK `DistinctBy(ConstraintName)` (absent for check constraints) is
  correct given `sys.foreign_key_columns`' per-column-pair row shape versus
  check constraints' non-duplicated one.
- `WindowFunctionArgumentScanner` — `LAG`/`LEAD` negative-offset detection
  and `PERCENTILE_CONT`/`PERCENTILE_DISC` out-of-range detection match Msg
  8730/8727 semantics; the shared literal-folding logic correctly handles
  unary-minus and arithmetic forms; the `[0,1]` inclusive boundary check is
  correct at both endpoints.

---

## Not yet audited

None — all 78 rule scanner families have been audited at least once as of
this pass. Re-auditing after a shipped fix, or auditing a newly added rule
family, restarts this list.
