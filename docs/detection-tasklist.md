# SilentScan detection checklist

Open work and the decisions that close it. The research behind it — anti-pattern
space, incumbent survey, measured engine facts, calibrated thresholds, killed
candidates — is in `detection-reference.md`. Every shipped rule is in
`rules.html`.

A shipped item's entry is deleted, not annotated. Only two things outlive an
item: a fact that can't be re-derived from the code, and a decision that
would otherwise be re-proposed — both move to `detection-reference.md`'s
Settled section.

Competitor tools are referred to generically; real identities are in
`vendor/tool-references.md` (gitignored).

---

## Open work

### Detections

- [ ] **`WriteLossNumericScaleNarrowingRuleId`'s "silent, no error" claim is
      false under `NUMERIC_ROUNDABORT ON`.** Oracle-confirmed:
      `DECLARE @d DECIMAL(5,2) = 123.456` silently rounds to `123.46` when
      `NUMERIC_ROUNDABORT` is `OFF` (the default), but raises `Msg 8115,
      Arithmetic overflow error` when it's `ON`. Scoped narrowly to
      same-family numeric/decimal scale narrowing - confirmed *not* to
      affect `INT` truncation, `VARCHAR` length truncation, or
      `FLOAT`-to-`DECIMAL` narrowing under the same setting.

- [ ] **`READ_COMMITTED_SNAPSHOT` × `READCOMMITTEDLOCK` table hint.**
      `ControlFlowRiskScanner.cs` matches `TableHintKind.NoLock`/
      `ReadUncommitted` for dirty-read risk but not `ReadCommittedLock`. On
      a database with RCSI on (`sys.databases.is_read_committed_snapshot_on`,
      catalog-decidable), `READCOMMITTEDLOCK` silently reverts that one
      query from row-versioned to blocking/locking reads - a real
      concurrency/consistency change invisible from the rest of the batch.

- [ ] **`DURABILITY = SCHEMA_ONLY` memory-optimized tables: zero
      coverage.** Pure DDL-time fact, no live-database dependency. A table
      declared `WITH (DURABILITY = SCHEMA_ONLY)` loses all data on
      restart/failover - about as squarely "silent data loss" as this
      tool's mission gets, and nothing currently checks for it despite the
      rest of the memory-optimized-table rule family already existing.

- [ ] **`RemovedSecurityStoredProcedureNames` is missing two engine-tracked
      names.** Diffed the full list against `sys.dm_os_performance_counters`
      `'Deprecated Features'` (255 entries, authoritative for the exact
      running engine version): `sp_change_users_login` and
      `sp_changedbowner` are tracked deprecated security/user-mapping
      procedures, same flavor as `sp_addlogin`/`sp_grantdbaccess` already
      in the set, but absent from it. (The same diff also found three of
      our entries - `sp_dropalias`, `sp_helprotect`, `sp_helpuser` - not in
      the engine's current tracked list; not necessarily wrong to keep,
      just not corroborated by this source.)

- [ ] **Legacy LOB statements (`READTEXT`/`WRITETEXT`/`UPDATETEXT`/
      `TEXTPTR`/`TEXTVALID`) have zero coverage anywhere.** Confirmed by two
      independent methods: none of the five appear as a referenced AST node
      type anywhere in `src/`, and all five are official
      engine-tracked deprecated features (same `sys.dm_os_performance_counters`
      diff as above).

- [ ] **Dynamic Data Masking has zero coverage as a feature area** - not one
      missing case, no references anywhere in Core at all (no check for
      masked-column arithmetic/comparison exposure, no check for
      `default()`'s per-type sentinel values e.g. `1900-01-01` for
      `DATETIME`, oracle-confirmed). Scope, not a single bug: needs its own
      pass to decide what's decidable and worth the precision bar, not a
      quick patch. Separately, oracle-confirmed the engine recognizes an
      undocumented fifth masking function name, `datetime()` (parses,
      arity-checked, distinct from the four publicly documented functions
      `default`/`email`/`random`/`partial`) - noted here so it isn't
      rediscovered from scratch if this area gets scoped later.

- [ ] **`ALLOW_ROW_LOCKS`/`ALLOW_PAGE_LOCKS = OFF` is a hidden concurrency
      hazard, zero coverage.** Both are plain DDL-declared/catalog-visible
      facts (`sys.indexes.allow_row_locks`/`allow_page_locks`), fully
      decidable, currently referenced nowhere except as a column name in
      `SystemCatalogViewRegistry`. An index built with either `OFF` forces
      page- or table-level locking for any DML touching it - a plain
      `UPDATE`/`DELETE` statement gives no hint of this, so two statements a
      developer assumes can run concurrently against unrelated rows can
      block or deadlock instead. Same shape as the `READCOMMITTEDLOCK`/RCSI
      gap already on this list - a table-hint/index-option silently
      reverting locking granularity in a way invisible from the DML site.

- [ ] **`STATISTICS_NORECOMPUTE` index option is a distinct staleness gap
      from `MissingStatisticsScanner`.** The shipped rule catches "no
      statistic exists and auto-create is off." An index built `WITH
      (STATISTICS_NORECOMPUTE = ON)` has a statistic that exists but is
      pinned - it is never auto-refreshed regardless of the database's
      `AUTO_UPDATE_STATISTICS` setting. Same downstream symptom (stale
      cardinality estimate), different DDL surface, currently zero coverage.

- [ ] **`CURSOR_CLOSE_ON_COMMIT` - zero coverage, narrow blast radius.**
      When `ON`, any open cursor is silently closed by the next
      `COMMIT`/`ROLLBACK` - a script that opens a cursor, commits mid-flow,
      then keeps fetching from it errors at runtime with no hint from the
      cursor-open site itself. Real and decidable (session setting × cursor
      lifetime across a commit), but only reachable by scripts that mix
      cursors with a mid-flow commit - low priority relative to the rest of
      this list, recorded so it isn't rediscovered from scratch.

- [ ] **`STRING_SPLIT` separator must be exactly one character.** A literal
      (or constant-folded) separator argument of any length other than 1 is
      a compile-error fact, pure source-level analysis, no catalog needed.

- [ ] **`ColumnstoreUnsupportedColumnTypeRuleId` may be narrower than the
      real engine gate.** Currently fires only for `SQL_VARIANT`. The
      underlying columnstore type-support check is reportedly broader (a
      wider disallowed-type set, and at least one type gated behind a
      feature switch rather than an unconditional ban) — widen only after
      each additional type is independently oracle-confirmed the same way
      `SQL_VARIANT` was.

- [ ] **`ProcCallArgumentMismatchRuleId` sibling: table-valued parameter
      column-shape mismatch.** The shipped rule covers a scalar EXEC
      argument marshalling mismatch; a statically resolved TVP call whose
      supplied table variable/expression column metadata (type, length,
      precision, scale, collation, nullability, by ordinal) differs from the
      procedure's declared TVP type is the same silent-marshalling-mismatch
      family, uncovered today.

- [ ] **Partition function parameter type mismatch.** A partitioned object's
      partitioning column with a catalog type/precision/scale/length/
      collation that does not exactly match its partition function's own
      parameter type is a clean catalog join
      (`sys.partition_functions`/`sys.partition_parameters`/
      `sys.index_columns`/`sys.columns`) proving a real DDL-time mismatch.

- [ ] **`WriteLossClassifier` sibling: cursor `FETCH INTO` binding loss.** A
      cursor's `FETCH INTO` variable binding that statically loses precision
      or truncates against the cursor's own defining `SELECT` expression
      type is the cursor-FETCH analogue of the shipped write-loss family and
      should reuse `WriteLossClassifier` rather than being designed from
      scratch.

- [ ] **`ColumnCollationDriftRuleId` sibling: `sys.columns.is_ansi_padded`
      structural fact.** A variable-length character/binary column's own
      `ANSI_PADDING` state is fixed at creation and stays sticky regardless
      of later session settings — catalog-only, distinct from the shipped
      session-level `SetOptionFindingKind` ANSI_PADDING rule, same family
      shape as `ColumnCollationDriftRuleId`.

- [ ] **`GROUPING`/`GROUPING_ID` argument absent from the query's own
      `GROUP BY` list.** Pure syntactic fact provable from the parse tree,
      no catalog dependency.

- [ ] **Recursive CTE anchor/recursive branch type disagreement.** A
      recursive CTE column whose resolved type disagrees between the anchor
      and recursive branches is decidable by reusing
      `ExpressionTypeInferencer` across both branches — a genuine
      compile-time defect, not currently checked.

- [ ] **`VariableLengthKeyColumnExceedsKeyLimit` sibling: table in-row row
      size exceeds the engine's fixed limit.** A table whose summed maximum
      in-row column widths (catalog + type metadata) exceed SQL Server's
      fixed in-row row-size limit is a pure catalog computation, the
      row-level analogue of the shipped index-key-length rule.

- [ ] **CLR table-valued function signature drift.** A CLR TVF's declared
      SQL signature disagreeing with its referenced assembly method's real
      signature (`sys.assembly_modules`/`sys.assembly_parameters`) after an
      independent code change on either side is a real, decidable staleness
      check with no shipped equivalent.

- [ ] **New rule family: system-versioned temporal period-column contract
      violations.** Distinct from the shipped `TemporalTableHistoryIndexGapRuleId`
      (which only checks index mirroring): a `SYSTEM_TIME` or SQL:2011
      `BUSINESS_TIME` (application-time) period declaration with a missing,
      non-`GENERATED ALWAYS`, or precision/collation-mismatched period
      column violates the engine's own period-column contract at DDL time.
      Ships as one family covering both period kinds.

- [ ] **`StringConcatNullRuleId` sibling: XML generation NULL coercion.**
      `FOR XML`/`.value()`-style XML generation silently coercing a nullable
      source to empty string, with no explicit NULL policy, is the same
      class of silent-NULL-handling divergence as the shipped
      `StringConcatNullRuleId` — reuse its nullability-analysis approach.

- [ ] **New rule family: `REGEXP_INSTR`/`REGEXP_REPLACE`/`REGEXP_LIKE`/
      `REGEXP_SUBSTR` reject a MAX-typed argument at bind time.** A
      `VARCHAR(MAX)`/`NVARCHAR(MAX)` argument to any of the four `REGEXP_*`
      functions is a clean, statically decidable compile-error fact from
      argument types alone — same shape as `MaxTypedColumnRuleId` but for
      this function family. Ships as one rule covering all four.

- [ ] **New rule family: bounded string builtins with constant-provable
      truncation (`REPLICATE`/`REPLACE`/`SPACE`/`TRANSLATE`).** Each
      function's non-MAX-typed result is capped at 8000 bytes; when every
      operand controlling the result length is a compile-time constant, the
      exact result length is constant-foldable, so an overflow past 8000
      bytes is provable with no runtime data — sibling to the shipped
      `WriteLoss` family. Ships as one rule family across the four
      functions.

- [ ] **New rule family: predicate provably contradicts a trusted `CHECK`
      constraint/`NOT NULL`/catalog-proven equality, making the result set
      empty.** Distinct from the shipped literal-only
      `AlwaysTrueOrFalseLiteralComparisonRuleId`: a statement predicate on a
      constrained column that is provably disjoint from a trusted `CHECK`
      constraint's interval, or contradicts a `NOT NULL` fact, or a
      catalog-proven equality/constant constraint, makes that branch's
      result set provably empty — a genuine, catalog-decidable dead-
      predicate finding. Needs an oracle check of exactly which constraint
      shapes the optimizer itself proves unsatisfiable (vs. ones this tool
      would be asserting without engine confirmation).

- [ ] **`OUTER JOIN` predicate on the null-supplying side silently collapses
      to an `INNER JOIN`.** A predicate that references an outer join's
      null-supplying side and rejects `NULL` (no explicit `OR col IS NULL`
      guard) makes the query equivalent to an `INNER JOIN`, defeating the
      author's evident intent to preserve unmatched rows — a well-known,
      statically decidable correctness footgun (predicate nullability
      against join side) not currently caught by any shipped rule.

- [ ] **`CartesianJoinRuleId` sibling: `INNER JOIN` `ON` predicate provably
      `FALSE`.** The complementary case to the shipped cartesian-join
      family (always-`TRUE`/no-predicate): an `INNER JOIN`'s `ON` predicate
      that folds from constants and fixed engine semantics to `FALSE` can
      never produce a row — same constant-foldable-condition mechanism the
      shipped cartesian rules already use.

- [ ] **`INSERT`/`UPDATE` explicitly assigns a `GENERATED ALWAYS` (temporal
      period) column.** A DML target list explicitly assigning a value to a
      catalog-identified generated-always column is a hard compile/runtime
      error — clean catalog join (generated-always column metadata
      intersected with the DML target list), same shape as the shipped
      oracle-confirmed hard-error rules.

- [ ] **`TemporalTableHistoryIndexGapRuleId` sibling: history-table column-
      mapping mismatch.** A system-versioned table's history table with an
      incompatible column mapping against the current table (ordinal, type,
      nullability, or generated-role mismatch) is a real, catalog-decidable
      structural defect — the column-mapping sibling of the shipped
      history-index-gap rule.

- [ ] **`ALTER SCHEMA TRANSFER` against a system-shipped or protected
      object.** Decidable purely from catalog (`is_ms_shipped`/object class)
      at the transfer statement — the same "oracle-confirmed hard error
      before any data-dependent check" shape as the shipped ALTER TABLE
      SWITCH family.

- [ ] **`TempTableExecShapeColumnTypeMismatchRuleId` sibling:
      `EXEC ... WITH RESULT SETS` shape mismatch.** A statically resolved
      `WITH RESULT SETS` clause whose declared column count/types disagree
      with the procedure's real, engine-described result-set shape
      (`sys.dm_exec_describe_first_result_set`, same technique the shipped
      `INSERT INTO #temp EXEC` rules already use) is the `WITH RESULT SETS`
      analogue of that shipped family.

- [ ] **Lower-confidence/niche backlog from the 2026-08-22 gap survey — one
      line each, group before scoping.** These didn't clear the bar for a
      full write-up above (medium/low survey confidence, a narrower feature
      area, or "verify it isn't already covered" rather than a clean new
      gap) but are real enough not to drop silently. Each still needs its
      own oracle confirmation before design.
      - Verify the shipped predicate-survival algebra already folds `LIKE`
        patterns into its interval model, and already flattens nested
        AND/OR trees (not just direct conjuncts/disjuncts) before treating
        those as closed — may already be covered by the recent commit.
      - `ScalarUdfInlineabilityScanner`: the survey claims it covers only
        about half the engine's real inlineability checks; beyond the two
        gaps already written up above (compat-level gate, re-eval-count
        threshold), enumerate the remaining checks against the scanner's
        own visitor one at a time rather than acting on the vague aggregate
        figure.
      - `VerdictClassifier.IsOutOfModelCategory` returns `Unknown` (never an
        actionable finding) for XML/JSON/UDT/legacy-LOB comparisons where
        the engine's own comparability gate hard-rejects the comparison —
        check this against the oracle-probed type matrix; if real, these
        should reclassify as `OperandClash`, not stay `Unknown`.
      - New family: an assignment (`SET`/`INSERT`/`UPDATE`) whose source
        type cannot legally implicit-convert to the target at all
        (encryption-state mismatch, illegal collation coercion, legacy-LOB
        ineligibility) is a compile-time reject, a stronger and distinct
        claim from `WriteLossFinding`'s "compiles but silently loses data".
      - `ProcCallArgumentMismatchRuleId`: the reverse direction — a
        callee's `OUTPUT` parameter's real assigned value marshalled back
        into the caller's receiving variable — is currently uncovered;
        mechanism needs pinning down before scoping.
      - New family: online DDL blocked by column type. `ALTER COLUMN`/
        `ALTER TABLE`/`ALTER INDEX ... REBUILD`/`DROP INDEX` with `ONLINE`,
        and a whole-table online rebuild, are all documented to reject a
        legacy-LOB/CLR-incompatible column type shape — one consolidated
        rule, not four/five separate ones.
      - New family: partition/filegroup DDL alignment siblings to the
        shipped `ALTER TABLE SWITCH` family — partition-`REBUILD` alignment
        mismatch, `DROP` against a non-updateable (read-only/offline)
        filegroup, FILESTREAM data-space compatibility mismatch, a
        partition scheme's columns disagreeing with the partitioning
        columns, and a compile-time-foldable partition number exceeding the
        engine's 14999 ceiling.
      - `CREATE TRIGGER` on a FILESTREAM-backed table failing at DDL time.
      - `NonPersistedComputedColumnRuleId`/`TryCastComputedColumnPredicateRuleId`
        sibling: the direct DDL-time hard failure when an indexed view or
        indexed computed column references a nondeterministic expression —
        check whether this boundary is already caught downstream.
      - `UNPIVOT` mixing source columns with incompatible types
        (`sys.columns`-decidable).
      - Memory-optimized (Hekaton) natively compiled module restrictions
        distinct from the shipped table-level family (unsupported column
        type, unsupported index option, cross-storage/CASCADE foreign key):
        the fixed row-size ceiling for a memory-optimized table, CLR UDT/
        function binding inside a natively compiled module, "deep type"/
        unsupported-builtin binder rejection inside a natively compiled
        module, non-Unicode-with-UTF-8-collation rejection in a natively
        compiled module, and an unsupported `GENERATED ALWAYS` variant —
        each needs its own oracle confirmation; likely higher-effort than
        the shipped catalog-only family since a module body's own
        expressions have to be walked, not just the table's own catalog
        shape.
      - `WITH SCHEMABINDING` referencing an alias user type (or an invalid
        parsed type name) is a documented restriction.
      - Full-text index DDL validation (unsupported column type, invalid
        language id, nondeterministic computed column, >1024 indexed
        columns) — real but needs new full-text-index modeling in the
        catalog builder that doesn't exist today.
      - Always Encrypted per-type restrictions beyond the comparison/index
        family already written up — a column type the engine's own
        encryption-support rules reject outright.
      - `TemporalTableHistoryIndexGapRuleId`-family sibling: current/history
        table schema divergence (type/precision/scale/collation/encryption)
        blocking temporal validation, distinct from the column-mapping gap
        already written up.
      - Sparse column type/compression restrictions (`sys.columns.is_sparse`
        plus table compression state) — allow-list is version-dependent.
      - Legacy LOB type (`text`/`ntext`/`image`) paired with a surrogate-
        aware or UTF-8 collation.
      - New family: `STRING_SPLIT`/`REGEXP_MATCHES`-style string TVF
        argument-type and MAX-width validation, and `STRING_SPLIT`'s
        3-argument ordinality form being version-gated — fold together
        with the shipped-candidate `REGEXP_*` MAX-argument family above
        into one string-TVF argument-validation rule.
      - Semantic Search TVFs (`SEMANTICKEYPHRASETABLE` etc.) requiring a
        qualifying full-text semantic index — legacy/rarely used feature.
      - New family (SQL Server 2025): `JSON_VALUE(...RETURNING...)`/
        `JSON_CONTAINS` exact-match predicate shapes eligible for a JSON
        index rewrite — the JSON-index sargability counterpart to the
        shipped `IndexCoverageKeyLookupProneIndexRuleId` family; needs an
        oracle matrix for what "exact match" precisely means on a brand-new
        feature.
      - `CollationConflictRuleId`: confirm `GREATEST`/`LEAST` (2022+)
        arguments are actually walked by the existing collation-conflict
        predicate walker — genuinely incompatible collations there should
        already report but may not.
      - Broaden the float-non-determinism family (aggregate argument,
        already written up) to float-typed arithmetic operands generally
        and float constants in precision-sensitive expressions — likely one
        rule, not three.
      - `REVERT WITH COOKIE = @x` requiring `@x` to be a fixed-size
        `varbinary` matching the engine's cookie type/size is decidable
        from the variable's own declaration.
      - Broaden the `LAG`/`LEAD`/`PERCENTILE_*` constant-argument-validation
        family (already written up) to cover any compile-time-constant
        percent-like argument outside the inclusive 0-100 range (e.g.
        `TABLESAMPLE PERCENT`) — same mechanism, one family.
      - `FOR XML` forbidden option combinations (e.g. `EXPLICIT` with inline
        XSD) — decidable purely from the statement's own option list, no
        catalog access needed.
      - New `SecurityFindingKind`: `sp_invoke_external_rest_endpoint` is a
        real outbound-network call surface distinct from the shipped
        hardcoded-IP-address finding.
      - `sp_execute_external_script`'s `WITH RESULT SETS`-style column
        declaration reusing a name, omitting a required type binding, or
        declaring a rejected type.
      - `OPENJSON WITH` schema projecting a native `json`-typed column
        while the enabling feature switch is off.
      - `VECTOR_DISTANCE`-family calls with a large-object-typed operand
        (SQL Server 2025 vector feature).
      - `OPENXML`/`OPENROWSET WITH` schema resolving a column to a type the
        engine's fixed type gate rejects (`sql_variant`/spatial/legacy-LOB)
        — one rule covering both clauses.
      - `AnsiPaddingMismatchRuleId`: the shipped rule only covers `LIKE`
        trailing-whitespace matching; the same trim/no-trim boundary
        reportedly also affects join matching, equality, and persisted-
        expression results more broadly.
      - `EXECUTE AT DATA_SOURCE` (elastic query) with a large-object-typed
        parameter.
      - Informational, database-configuration tier: an active
        `sys.plan_guides` row whose hints alter optimization/parameterization
        for in-scope application SQL.
      - External file-format/data-export partition column type restrictions
        (PolyBase/CETAS external-table column-type and virtual-column
        allow-lists; data-export partition column resolving to a large
        object or unsupported type) — real but niche.
      - A statically-known boolean element inside a JSON literal converted
        to the native `VECTOR` type (SQL Server 2025 feature, narrow).
      - A full-text predicate (`CONTAINS`/`FREETEXT`) used inside an
        aggregate/`GROUP BY` scope the engine rejects.
      - A window `PARTITION BY` expression resolving to a type SQL Server
        cannot compare for partitioning (LOB/XML/spatial).
      - New family: `CHANGE_TRACKING` restrictions — `ALTER TABLE ...
        ENABLE CHANGE_TRACKING` against a table carrying an Always Encrypted
        column, and change tracking already enabled on a table carrying a
        legacy LOB column (matches a real engine-emitted warning).
      - `ProcCallArgumentMismatchRuleId` sibling: a streaming/inline TVF's
        own parameter boundary needing an implicit conversion, the same
        silent-marshalling family applied to a different call-site kind.
      - `SessionDateSettingRuleId(DateFormat)` may be scoped too narrowly:
        the shipped rule only fires when the module's own body contains an
        explicit `SET DATEFORMAT`, but an ambiguous string-to-date
        conversion is reportedly session-format-dependent under
        compatibility level > 99 even with no `SET DATEFORMAT` present in
        the module — a real under-detection gap if confirmed, not just a
        new rule.
      - Fold into the bounded-string-builtins family already written up:
        `STRING_AGG`'s result type is capped at `VARCHAR(8000)`/
        `NVARCHAR(4000)` when none of its operands are MAX-typed, regardless
        of row count — a structural type-level fact, not row-count-dependent.
      - New family: `NVARCHAR` to a UTF-8-collation `VARCHAR` conversion (and
        the reverse) can expand/contract byte length past the declared
        target's 8000-byte cap — distinct failure mode from
        `WriteLossUnicodeReplacementRuleId` (byte-length overflow, not
        codepage `?` replacement); needs an oracle pass on exact truncation
        behavior.
      - Explicit `INSERT`/`UPDATE`/`MERGE` assignment to a SQL Graph node/
        edge table's own `$node_id`/`$edge_id` system column.
      - Heavier-lift candidate: a joined table catalog-provably contributing
        nothing (no projected columns/predicates/grouping/ordering, and
        FK/uniqueness/nullability prove it can't change multiplicity or
        null-extension) — real simplification finding, but the conservative
        multiplicity/null-extension proof is substantial engineering, not a
        quick win.
      - `QueryAntiPatternLinkedServerOrCrossDatabaseReferenceRuleId`:
        sharpen the existing "close to a guess" framing — a linked-server/
        remote-query source reportedly gets a fixed exactly-1-row
        cardinality estimate, an oracle-confirmable mechanical fact rather
        than a vague warning, the same precision upgrade already done for
        the table-variable-low-compat-estimate rule.
      - `CheckConstraintNullNotHandledRuleId`-family sibling: a DML
        statement against a `WITH CHECK OPTION` view whose inserted/updated
        values are provably contradicted by the view's own predicate —
        confidence is only medium/unverified; likely detectable for literal
        values only in practice.
      - `DeprecatedSyntaxDeprecatedSetRowcountRuleId` is scoped too
        narrowly: it only warns `SET ROWCOUNT` will stop being honored by
        DML in a future release, but a nonzero `SET ROWCOUNT` left active
        silently limits rows affected/returned by every subsequent
        statement right now — a present-tense correctness risk, not just a
        future-deprecation one.
      - `DBCC RULE ON/OFF` toggles the same legacy `CREATE RULE`/
        `sp_bindrule` mechanism already flagged elsewhere as deprecated —
        using it at all is the same decidable deprecated-syntax fact.
      - Ledger tables restrict which `ALTER COLUMN` shapes are legal
        (`sys.tables.is_ledger_on` plus before/after column shape) — narrow
        feature.
      - `DanglingObjectReferenceRuleId` sibling: a CLR aggregate whose
        catalog-registered `Terminate`/`Accumulate` method can no longer be
        resolved after `ALTER ASSEMBLY` fails only on first invocation —
        same deferred-resolution shape, but CLR aggregates are rare.
      - `CREATE`/`ALTER XML SCHEMA COLLECTION` binding a column to a
        disallowed scalar type.
      - New consolidated rule: CLR UDT catalog-metadata validity — two UDT
        signatures treated as interchangeable when they aren't, a
        referenced UDT method that can't be resolved, an incompatible CLR
        array conversion, a UDT participating in an operator its metadata
        doesn't support. Hand-authored CLR UDTs beyond the built-in spatial
        types are rare, so low real-world hit rate.
      - `sp_cursoropen`/`sp_cursorexecute` called with a literal scroll-
        option bitmask or `paramdef` shape the engine rejects — usually
        client-driver-generated rather than hand-authored, low value.
      - `BACKUP`/`RESTORE` and `CREATE DATABASE` forbidden option
        combinations, decidable purely from the statement's own option
        list — DBA-maintenance-script scope, not typical application SQL.
      - `IndexDesignRuleId` sibling: an index definition repeating the same
        column across its partition/key/include/order-by lists.
      - PolyBase/Hadoop external-table column-type and virtual-column
        restrictions — mainstream on-prem feature but low adoption.
      - A typed XML variable resolving to a different/missing schema
        collection than its type metadata records — rare in normal
        authoring.
      - New consolidated family, sibling to `DanglingObjectReferenceRuleId`:
        an object protected from `DROP` by dependents or protection state —
        `DROP ROLE` targeting a protected fixed role while protection is
        active, `DROP SCHEMA` on a non-empty schema, `DROP EXTERNAL DATA
        SOURCE`/`DROP EXTERNAL FILE FORMAT` blocked by a dependent external
        table/stream.

- [ ] **ALTER TABLE SWITCH: indexed-view alignment (Msg 11400-11405).**
      Catalog-decidable in principle (all facts live in `sys.indexes`/
      `sys.views`/the view's own definition text - no execution required),
      but genuinely more work than every other item in this family: needs (a)
      a base-table -> indexed-view REVERSE lookup (today's
      `_indexedViewIndexesByQualifiedName` is keyed by the view's own name,
      not which base table(s) it references - that direction doesn't exist
      yet), (b) whether the indexed view is "aligned" with a table's own
      partitioning (same partition function, and the view's partitioning
      column must be a DIRECT selection of the table's partitioning column,
      not an expression/derived one - provenance through the view definition,
      the same kind of analysis `ComputedColumnMatcher`/lineage-layer
      resolvers already do elsewhere, but not yet wired to this specific
      question), and (c) requiring the source table to have a MATCHING
      indexed view (by the same alignment test) for every one the target has.
      Getting "equivalent partition function" or "directly selected column"
      subtly wrong risks a false positive, which this project's precision
      discipline treats as worse than a missed true positive - deliberately
      not attempted in the same pass as the rest of this family. Scope
      properly (probably 2-3 oracle probes: direct-vs-expression partitioning
      column, non-equivalent partition function, reference-count mismatch)
      before implementing.

---

## Out of scope

Production-only signals — the one real exclusion under CLAUDE.md's scope rule.
Parameter sniffing depends on the runtime data distribution and on which value
first compiled the plan; runtime-only signals (spills, memory grants, execution
frequency, stale statistics, plan-cache duplication, row-estimate mismatch)
don't exist until a query runs. The static risk factors for sniffing ship as
their own findings.

---

## For every new stream

Thresholds are calibrated against the real measured distribution, never copied
from convention. Record the calibration in `detection-reference.md`
Appendix 10.

Before writing (or re-auditing) a rule whose rationale depends on a session
setting, a keyword list, a data-type set, or a fixed option enum: find the
engine's own closed enumeration for it - a ScriptDom enum, a live catalog/DMV
(`sys.dm_os_performance_counters`, `sys.configurations`,
`sys.database_scoped_configurations`), or an internal engine table via
`vendor/sql2025` (target the function that *consumes* a fixed-size table,
not a decompiled enum's member names - those rarely survive compilation) -
and diff every member against the rule's actual coverage. Free-text sources
(`sys.messages`) don't work for this: there's no reliable way to rank them by
relevance, so don't try. Oracle-verify every surviving candidate before
trusting it - a structurally-missing case is not yet a confirmed gap.
