# SilentScan detection checklist

Open work and the decisions that close it. The research behind it — anti-pattern
space, incumbent survey, measured engine facts, calibrated thresholds, killed
candidates — is in `detection-reference.md`. Every shipped rule is in
`rules.html`.

A shipped item's entry is deleted, not annotated. Only two things outlive an
item: a fact that can't be re-derived from the code, which moves to
`detection-reference.md`, and a decision that would otherwise be re-proposed,
which becomes one line under Settled.

Competitor tools are referred to generically; real identities are in
`vendor/tool-references.md` (gitignored).

---

## Open work

### Detections

- [ ] **Static risk factor: persisted computed column on a spatial expression,
      disabled by a future compat-level change.** `sys.dm_db_objects_disabled_on_compatibility_level_change(@level)`
      (real, documented: Microsoft Learn confirms it flags indexes/constraints
      containing a persisted computed column whose expression uses a spatial
      UDT method — dropping/disabling on a compat-level change) would be a
      genuine, catalog-detectable static fact if it can be reproduced.
      **Not yet confirmed real — 3 honest oracle attempts against the local
      instance (2026-08-20) all returned zero rows**: a persisted computed
      column calling `geography::STDistance()` with a supporting non-spatial
      index, tested across two real compat-level downgrades (160→100,
      160→120), never appeared in the DMV's own output, despite matching the
      documented general shape. Either the real trigger needs a genuine
      spatial index specifically (not a plain index on the computed column),
      a different spatial method whose result actually differs across compat
      levels, or a specific historical compat-level boundary this instance's
      version range doesn't span. Do not build this rule until a real,
      reproducing positive case is found — the DMV's existence and general
      documented purpose are confirmed, but the exact firing condition isn't,
      and this project ships nothing it can't oracle-confirm.

- [ ] **Static risk factor: table-typed parameter defeats PSP (Parameter
      Sensitive Plan, compat 170+).** `sys.dm_xe_map_values('psp_skipped_reason_enum')`
      (2026-08-20 survey, public/documented — 40 named reasons) lists
      `TableVariable` as a real, engine-recognized reason PSP optimization is
      skipped for a statement. A table-valued PARAMETER is visible statically
      from the parameter list — the same signal `ScalarUdfInlineabilityScanner`'s
      table-valued-parameter check already uses — so "this proc/function can
      never get per-value-shape plan variants because one of its parameters is
      table-typed" is a real, provable-from-code static fact, not a runtime
      signal, and fits CLAUDE.md's explicit carve-out ("The static risk factors
      for sniffing ship as their own findings"). Not yet scoped: which existing
      finding family this belongs in (a new `PspFindingKind`, or an addition to
      an existing parameter-sniffing-adjacent stream —
      `LocalVariablePredicateFinding`/`ParameterReassignmentPredicateFinding`
      are the current members of that family); which of the other 39 reasons
      are similarly static and provable (`HasLocalVar`(11) and `TableVariable`(8)
      look promising by name; most others — `LoadStatsFailed`, `SkewnessThresholdNotMet`,
      `CompilationTimeThresholdExceeded` — read as clearly runtime/data-dependent
      and out of scope); needs its own oracle probe pair and calibration
      before shipping, per "For every new stream" below.
      **Blocked (2026-08-20, confirmed directly): the local Docker instance
      cannot oracle-verify this at all.** PSP is compat level 170+; this SQL
      Server 2022 (16.0.4236.2) instance rejects `ALTER DATABASE ... SET
      COMPATIBILITY_LEVEL = 170` outright (Msg 15048 — valid values top out
      at 160). No PSP-aware plan/XEvent exists to probe on this build at
      all, so "TableVariable" (or any of the other 39 reasons) cannot be
      verified against a real engine here — only against the DMV's name,
      which is exactly the kind of unverified claim CLAUDE.md's precision
      discipline forbids shipping. Do not build this rule until a SQL
      Server 2025 (or later, compat-170-capable) target is available to
      verify against; re-check `@@VERSION`/`ALTER DATABASE ... SET
      COMPATIBILITY_LEVEL = 170` before re-attempting, don't assume the
      infrastructure gap has closed. `sys.dm_xe_map_values('interleaved_execution_disabled_reasons')`
      surveyed the same day — separately confirmed (oracle-verified directly:
      a compile-only `SET SHOWPLAN_XML` plan for an MSTVF shows the stale
      100-row estimate, only a real-executed `SET STATISTICS XML` plan shows
      the interleaved-execution-corrected estimate) that `TvfFenceVerifier`'s
      existing plan-SHAPE-only check (never reads `EstimateRows`) is already
      correctly immune to this — not a bug, a confirmed-safe design, recorded
      here only so the fact doesn't need re-deriving.

- [ ] **ALTER TABLE SWITCH: fulltext-index restriction (Msg 4918).** Catalog-
      decidable in principle - `sys.fulltext_indexes` is a plain catalog view,
      readable live exactly like `sys.indexes`, no execution required. Blocked
      purely by test-environment coverage, not discoverability: the standing
      Docker oracle instance does not have full-text search installed
      (`SERVERPROPERTY('IsFullTextInstalled')` returns 0, confirmed
      2026-08-21), so the documented behavior ("ALTER TABLE SWITCH statement
      failed because the table '%.*ls' has fulltext index on it") cannot be
      oracle-confirmed here, and this project ships nothing it can't
      oracle-confirm. Do not build this rule until a full-text-search-capable
      target is available to verify against - re-check
      `SERVERPROPERTY('IsFullTextInstalled')` before re-attempting, don't
      assume the environment gap has closed. Sibling rules already shipped
      from the same `FCanSwitchPartitions` investigation: see
      `AlterTableSwitchColumnMismatch`/`IndexMismatch`/`ConstraintMismatch`/
      `TargetOnlyIndexRestriction`/`FilegroupMismatch`/`TemporalMismatch`/
      `RuleConstraint`/`CdcPartitionSwitch`/`PartitionFilegroupMismatch` in
      `QueryAntiPatternFinding.cs` for the established pattern (visitor hook
      on `AlterTableSwitchStatement`, source/target resolved via catalog,
      oracle-confirmed message text cited in the finding).

- [ ] **`SelfReferencingDmlRuleId` may over-fire on modern compatibility
      levels.** The rule's rationale claims the engine always inserts extra
      defensive plan work (an eager spool, or an extra sort) for any
      self-referencing INSERT/UPDATE/DELETE/MERGE. SQL Server documents at
      least two real bypasses this rule's own scanner (`SelfReferencingDmlScanner.cs`,
      no `Top`/`CompatibilityLevel` handling at all today) never checks: a
      `TOP(1) ... ORDER BY` guard on a table that won't be rewound can itself
      satisfy Halloween Protection without a spool, and — separately, gated on
      compatibility level 150+ plus table properties (no FILESTREAM columns,
      not replicated, no disqualifying constraint types) all readable from the
      catalog — a nest-ID-based tracking mode can skip the spool entirely.
      Verify against the local test database (compat 150+, a `TOP(1) ORDER BY`
      self-referencing `UPDATE`) whether the claimed defensive plan work still
      appears; if the bypass is real and reachable, either suppress the finding
      on that shape or downgrade its confidence — do not leave an oracle claim
      standing that a real, catalog-visible bypass contradicts.

- [ ] **New rule family: operand not legally comparable at compile time
      (distinct from sargability).** SQL Server rejects some operand shapes
      outright at bind time, independent of whether an index could seek them:
      an XML column, a legacy LOB (`TEXT`/`NTEXT`/`IMAGE`), or an
      Always-Encrypted column whose encryption state doesn't match its
      comparison partner, used in a comparison, `GROUP BY`/`ORDER BY`/`DISTINCT`,
      an `IN` list, `BETWEEN`, a `CASE`/`COALESCE`/`NULLIF` branch, or a
      built-in function argument. `ExpressionTypeInferencer` merges type
      category/precedence today but never checks whether the merged or
      compared types are actually legal together, so a statement that would
      never compile can pass through silently. Needs its own oracle matrix,
      one shape at a time (XML equality, LOB in `GROUP BY`, encrypted-vs-
      plaintext comparison, …) before any rule ships — several shapes may
      already have a documented, citable error message worth checking first.

- [ ] **New rule family: unsupported type/constraint inside a memory-optimized
      table or natively compiled module.** In-Memory OLTP tables reject a
      documented set of column types, constraint shapes, and index options
      that an ordinary table allows — a catalog-decidable structural fact
      (`sys.tables.is_memory_optimized`) with the same shape as the already-
      shipped MAX-typed-column/columnstore-unsupported-type rules. Needs its
      own oracle matrix against a real memory-optimized table (unsupported
      column type, unsupported constraint, a natively compiled module hitting
      a row-layout limit) before scoping — not yet attempted.

- [ ] **`ScalarUdfInlineabilityScanner` is missing two real engine gates and
      can over-claim inlineability.** (1) Scalar-UDF inlining (FROID) is
      itself gated behind database compatibility level >= 150 (SQL Server
      2019) — a UDF the scanner marks inlineable on an older compat level
      never actually inlines. (2) The engine caps the count of scalar
      sub-expressions inlining would need to re-evaluate (documented default
      threshold ~100); a UDF body whose branch count exceeds this can never
      inline regardless of what other checks pass. Both gates are
      catalog/source-decidable (`sys.databases.compatibility_level`, a static
      branch count over the UDF body) and the scanner has no handling for
      either today. Needs its own oracle matrix (compat 140 vs 150+ on an
      otherwise-inlineable UDF; a UDF built to sit just under/over the
      re-eval threshold) before scoping.

- [ ] **New rule family: compile-time cardinality ceilings on
      `GROUPING SETS`/`CUBE`/`ROLLUP`.** Each has a fixed, documented,
      purely syntactic compile-time limit independent of any table's real
      data (expanded `GROUPING SETS` combination count > 4096; `CUBE` column
      count > 12; `ROLLUP` column count > 4095) — the same decidability
      shape as the already-shipped missing-`MAXRECURSION` rule. Oracle-check
      the exact boundary and error text for each of the three before
      shipping.

- [ ] **New rule family: constant-foldable argument validation for
      `LAG`/`LEAD`/`PERCENTILE_CONT`/`PERCENTILE_DISC`.** `LAG`/`LEAD`'s
      offset argument must constant-fold to a non-negative value;
      `PERCENTILE_CONT`/`PERCENTILE_DISC`'s percentile argument must
      constant-fold to a value in `[0, 1]`. Both are pure source-level
      constant-folding with no catalog dependency, the same shape as the
      already-shipped literal-required `ForcedParameterization` findings.
      Needs an oracle check of the exact rejected boundary (is exactly `0`/`1`
      valid for percentile? is a non-literal but foldable expression treated
      the same as a literal?) before shipping.

- [ ] **`MaxTypedColumnRuleId`-family sibling: SELECTIVE XML INDEX value
      column too wide.** A selective XML index's value column resolving to a
      large object or a string over 900 bytes (`sys.columns` type/max-length)
      fails at CREATE/ALTER time — the same catalog-decidable structural-
      failure shape as the shipped MAX-typed-column/columnstore-unsupported-
      type family, for a feature that family doesn't cover yet.

- [ ] **`FloatEqualityRuleId` sibling: float/real column fed into an
      aggregate.** Distinct footgun from the shipped equality-predicate
      rule: parallel-plan accumulation order for `SUM`/`AVG`/etc. over a
      float/real column is not guaranteed, so the identical aggregate over
      identical data can return a different result across runs/plans.
      Catalog-decidable from the column's type and its use as an aggregate
      argument; needs an oracle check of which aggregate functions are
      actually affected (order-dependent accumulation) versus which aren't
      (e.g. `MIN`/`MAX`).

- [ ] **Always Encrypted comparison/index legality beyond the shipped
      `ORDER BY` rule.** Three related, catalog+config-decidable gaps in the
      same family as `AlwaysEncryptedOrderByRuleId`: (1) a comparison/join/
      predicate against an enclave-required AE column when secure-enclave
      support isn't configured on the connected server; (2) a procedure
      parameter compared against an AE column with incompatible declared
      type/length/collation/encryption metadata; (3) a RANDOMIZED-encrypted
      column (non-deterministic by design, incompatible with the ordering an
      index key requires) used as an index key column. Each needs its own
      oracle case — enclave configuration in particular may not be
      reproducible against every test target.

- [ ] **New rule family: `ALTER TABLE ALTER COLUMN` safety.** Two related,
      catalog-diffable DDL-time risks not covered by any shipped rule: (1)
      narrowing a numeric column's precision/scale, or a var-time column's
      fractional-seconds precision, below its current catalog-declared value
      (`sys.columns` before vs. the statement's target type) risks a DDL
      failure or silent truncation of existing data; (2) an `ALTER COLUMN`
      between string/wstring/binary families whose length, collation, or
      binary-flag differs needs an explicit `CAST` or the statement fails
      outright. Likely ships as one unified `ALTER COLUMN`-safety rule
      rather than two. Needs an oracle matrix per narrowing shape (numeric,
      var-time, string-family) before scoping.

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

- [ ] **`IIF()` branch type mismatch with a lossy implicit conversion.**
      `IIF()` branches whose resolved types differ, where the engine's
      chosen implicit conversion (by type precedence) is narrowing or
      precision-losing, is a currently-uncovered correctness gap distinct
      from `CASE` handling — decidable by reusing
      `ExpressionTypeInferencer`'s branch-merge logic, kept separate from
      arithmetic typing per the standing plan.

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
      - New family: memory-optimized (Hekaton) compatibility — column
        restrictions (prohibited flags, unsupported `GENERATED ALWAYS`
        variant, type, size limit), constraint/index-option restrictions,
        the fixed row-size ceiling (`0x1f7c` bytes), CLR UDT/function
        binding inside a Hekaton/natively-compiled context, "deep type"/
        unsupported-builtin binder rejection, and non-Unicode-with-UTF-8-
        collation rejection in a native-compiled module — the survey
        surfaced this same underlying restriction family from ~8 different
        entry points; design once as a single Hekaton-compatibility rule.
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

### Docs

- [ ] **Per-rule pages: fill the remaining ~60/234 rules.** Shipped:
      `RuleDocSite` (`helpUri` scheme, wired into SARIF `rules[].helpUri` and
      `driver.informationUri`, plus `HumanizeTitle` so the index/page never
      show a raw `silentscan/family/name` id as the display label) with its
      golden slug test; `docs/rules.html` is a family-grouped index linking to
      one generated page per rule under `docs/rules/`; each page is
      Sonar-shaped ("Why is this an issue?" / "How can I fix it?" with
      noncompliant/compliant SQL, plus a separate "Verified by an automated
      test" section only when a real checked-in fixture exists) via
      `RuleDocContent`/`RuleDocExample` (`src/SilentScan.Core/Reporting/
      RuleDocs/RuleDocContent.cs`) — one hand-authored file per rule under
      `RuleDocs/<Family>/<RuleName>.cs`, wired into `RuleDocCatalog.ByRuleId`;
      `rules-doc` prunes orphaned pages; a docs-are-current regeneration test
      (`RulesDocGeneratorTests`) byte-compares against `docs/`. 174/234 rules
      have a `RuleDocContent` entry today (tier1, verdict/scan-forced+range-
      seek, write-loss, tvf-fence, scalar-udf, a chunk of catalog/predicates/
      call-graph, query-anti-pattern, trigger-correctness, forced-serial,
      cross-module, correctness/dml/join/query singles, cartesian-join,
      naming, session-date-setting, hint, window-frame, view-ordering,
      temp-table, identity, declaration, security, module-compile-flag,
      control-flow-risk, statement-shape, database-configuration, lineage,
      dynamic-sql, the rest of catalog/predicate singles, index-design) — a
      rule with no entry still renders (short rationale only, humanized
      title, no fabricated fix/example section), just thinner. Remaining
      backlog: formatting/dead-code/duplication/deprecated-syntax/
      code-metrics (~50, lower value - mostly self-evident from their name).
      Also open: `helpUri` on the JSON findings schema (deliberately deferred
      behind the later findings-schema-unification pass, not piecemeal). Do
      family-by-family, each its own commit (the per-rule-file-in-its-own-class
      pattern parallelizes well across subagents - each batch just needs the
      exact `SarifRuleCatalog` constant + current Rationale/FixGuidance text
      per rule, handed out per family).

      Linking the rule page from the readable/console report: shipped for the
      5 finding-group headings that carry a real `Kind`-driven rule ID at
      their own call site (`Tier1Title`/`TvfFenceTitle`/`ScalarUdfTitle`/
      `ForcedSerialTitle`/`SetOptionTitle` in `ReadableScanReportWriter.cs`),
      via `RuleDocSite.Url(SarifRuleCatalog.*RuleId(group.Key))`. The other
      ~79 group headings aggregate multiple rule IDs under one heading with
      no single ID to hang a link on - linking those needs a real per-heading
      rule-id redesign, not attempted here.

### Architecture inversion

The 2026-08 audit's root cause, one problem behind ~15 findings: the pipeline
doesn't own the rules — every scanner is a free-standing `static Scan(...)`
that re-decides pipeline-level questions (name resolution, module identity,
skip honesty, crash behavior, ordering) for itself, and all the shared
machinery is opt-in. The inversion: a rule receives an already-resolved world
and returns findings; the pipeline owns everything else. Phases are ordered by
value-per-line and are each independently shippable; do them in order, commit
per phase (Phase 0 commits per fix).

- [x] **Phase 0 — precision hotfixes.** Straight bugs, no architecture; each
      needs its fires/clean fixture pair per the working agreements. Shipped —
      every numbered item below landed; only the "deferred to their own
      decision" trailer remains genuinely open.
      1. Shipped: all 16 `cteRelations: null` / `CteRelations: null` call
         sites across the original 11 scanners now resolve real CTE scope
         (`CteResolver.Resolve` over each statement's own
         `WithCtesAndXmlNamespaces`, threaded via a per-visitor CTE-scope
         stack where a `QuerySpecification` has no direct access to its
         enclosing `SelectStatement`'s WITH clause). Every fix proven
         against the pre-fix code with `git stash` before landing — three
         (`CatchAllPredicateScanner`, `PartialCompositeForeignKeyJoinScanner`,
         `ParameterReassignmentPredicateScanner`) turned out to be worse
         than "binds to the wrong table": `PartialCompositeForeignKeyJoinScanner`
         and `IndexHintScanner` had a SECOND, independent bypass
         (`DirectBaseTableResolver` re-resolving via the catalog directly,
         never consulting `FromScopeResolver`'s scope at all — fixed at the
         shared helper, closing six more callers at once, see item 9).
         `CatchAllPredicateScanner`/`ParameterReassignmentPredicateScanner`
         don't just stop misfiring once fixed — they now correctly
         attribute the finding to the CTE's true underlying column instead
         of the coincidentally-named real one.
      2. Shipped (decision kept): a parameter DEFAULT seeding is widened like
         a literal argument — but findings from either are GENUINE (the
         omitting caller really executes the default), so widening must never
         suppress them; it only adds the external-caller placeholder
         accounting. Do not re-propose "suppress findings from
         corpus-observed parameter values" as a false-positive fix.
      Deferred to their own decision, not forgotten: guard-correlated branch
      cross-product in `SqlTextValue.Concat`/`ForkAssemblies` — confirmed real
      (2026-08-20): `Concat` (`SqlTextValue.cs:151`) juxtaposes two Templates'
      `Pieces` with no correlation, so two variables each built by an
      `IF`/`ELSE` under the textually-identical guard (e.g. two separate
      `IF @Mode = 'A' ... ELSE ...` blocks assigning different variables,
      later concatenated) land in one Template as two separate
      `TemplatePiece.Choice`s sharing a `GuardText`; `ForkAssemblies` (:611)
      then cross-products them as independent, producing assemblies pairing
      one Choice's guard-true alternative with the other's guard-false
      alternative — combinations that can never occur at runtime. Still
      design-first, not a scoped fix: the obvious repair (correlate
      equal-`GuardText` Choices to the same alternative index) is itself
      unsound, because `GuardText` is rendered predicate text, not value
      identity — if the guarded variable is reassigned between the two
      `IF`s, two textually-identical guards are not the same runtime
      condition, and index-correlating them would silently drop a real
      reachable assembly (a false negative) to remove a false positive. A
      real fix needs guard identity (has anything the guard reads changed
      since its first occurrence), not `GuardText` string equality — no fix
      is scoped until that's designed. `sp_prepare`/`sp_execute` recognition
      (checked 2026-08-20: zero occurrences across the local test database's
      ~5,000-module real sample set — `sys.sql_modules.definition LIKE
      '%sp_prepare%'`/`'%sp_execute%'` both return 0; this driver-generated,
      ODBC-prepared-statement pattern essentially never appears in
      hand-written T-SQL, so a rule for it would ship unexercised, same
      reasoning as the partitioned-index item above — stays deferred, now
      with real evidence rather than only a stated design gap).
      9. Shipped: `DirectBaseTableResolver` (`ResolveDirectBaseTable`/
         `ResolveDirectBaseTables`/`ResolveDirectBaseTableName`) was its own,
         separate instance of the same bug class — it re-qualified and
         `catalog.Find`'d a table reference directly, never consulting
         `FromScopeResolver`'s scope at all, so no scanner's own
         `cteRelations` wiring could fix it. `PartialCompositeForeignKeyJoinScanner`
         and `IndexHintScanner` were rewritten to consult the already-
         resolved `byAlias` scope instead of re-deriving their own answer
         (no `DirectBaseTableResolver` call left). The other six callers
         (`AggregateDivisionColumnstoreScanner`, `FloatEqualityPredicateScanner`,
         `DuplicationScanner`, `StringConcatNullScanner`, `QueryAntiPatternScanner`,
         `NonUniqueUpdateSourceScanner`'s join-source side) were fixed by
         giving the shared helper itself a required `cteNames` parameter —
         a name in that set is declined (same as a view/derived table)
         rather than resolved against the catalog. Five of the six pass a
         file-wide `CteNameCollector.Collect` over the whole parse result
         (an intentional over-approximation: a CTE elsewhere in the same
         file can only cause an extra decline, never a false positive —
         the safe direction) since none had per-statement CTE tracking to
         begin with; `NonUniqueUpdateSourceScanner` already threads its own
         statement's `WithCtesAndXmlNamespaces`, so it collects precisely.
- [x] **Phase 1 — make naive resolution unrepresentable.** Shipped, narrower
      than originally scoped. The type system couldn't tell honest
      resolution from naive: a `ScopeEntry` from `cteRelations: null` looked
      identical to one resolved with full scope. Killed at compile time:
      `FromScopeResolver`'s flat `Resolve` overload lost its defaulted
      `ledger`/`cteRelations`/`procScope` parameters (all three now
      required), and `ResolutionContext.CteRelations` itself is no longer
      nullable — every construction site across the codebase must supply a
      real dictionary (possibly empty, never absent). Every call site
      already passed a real value by the time this landed (all sixteen
      Phase-0.1 sites fixed first), so the signature change touched zero
      call sites and needed exactly one real fix
      (`QueryExpressionResolver.ResolveQuerySpecification`'s own nullable
      `cteRelations` parameter, coalesced to empty) — the compiler proved
      the fleet was clean rather than finding new gaps, which is the
      point: a future call site that forgets `cteRelations` now fails to
      build instead of silently defaulting.
      Descoped from the original plan: full migration onto
      `ScopedSqlVisitorBase` (11 scanners, each with its own FROM/JOIN
      handling — real architectural work, not a compile-time gate, and
      belongs with Phase 2's rule harness instead) and the
      `StatementVariantParityTests`-style reflection backstop (tried;
      "does this visitor call `FromScopeResolver`" isn't a reflectable
      signal the way Create/Alter method-pair existence is, and "does it
      override `ExplicitVisit(QuerySpecification)`" produces mostly noise —
      dozens of unrelated scanners visit that node for reasons having
      nothing to do with FROM-clause resolution). The non-nullable
      parameter is the real gate; a reflection test would have been a
      weaker, noisier version of what the compiler already enforces.
- [ ] **Phase 2 — rule harness.** No `IRule` abstraction exists; 64 scanners,
      ad-hoc signatures, hand-wired in `ScanReportBuilder`'s 1,327-line
      method at 3 separate points each (+ SARIF/readable/RuleCatalog = ~9
      files per new rule); nothing enforces a rule is invoked (implemented-
      but-never-wired compiles green); zero catch blocks in Core, so one
      scanner throwing on one module kills the whole scan as an
      `AggregateException` the CLI handler at `ScanDbCommand.cs:132` doesn't
      match. Harness owns: registration (reflection-enforced: registered ⇔
      invoked ⇔ in `RuleCatalog`), per-rule×per-object containment (crash →
      ledgered unanalyzable, scan continues), skip-ledger threading by
      default (today 1 of 64 scanners records skips;
      `DirectBaseTableResolver.cs:31` documents its own silent exclusion for
      7 dependents), module identity (delete the 6 wrong private copies of
      `Qualify` — `ControlFlowRiskScanner.cs:271`, `CodeMetricScanner.cs:208`,
      `DuplicationScanner.cs:412`, `DeprecatedSyntaxScanner.cs:149`,
      `DeadCodeScanner.cs:122`, `FormattingScanner.cs:387` — which report
      `usp_X` where every other stream reports `dbo.usp_X`), central
      deterministic ordering (total-order comparator, one place), confidence
      filtering (the exact drift `ScanReportBuilder.cs:1238-1247` documents
      shipping once already). Absorbs the old "Separate rule decisions from
      ScriptDom traversal" item; its settled sub-decision stands: a generic
      `CollectorVisitor<T>` was designed and rejected — the `Flatten*`
      signatures diverge too much for it to pay.
- [ ] **Phase 3 — one findings schema, one emission path.** `ScanReport` is a
      76-positional-list record each writer hand-picks from; SARIF (the CI
      gate) references none of `SkippedConstructs`/`DynamicSqlSummary`/
      `TypedPredicateSummary`, so "couldn't look" is
      indistinguishable from "clean" exactly where the contract forbids it.
      Collapse to findings + summaries consumed uniformly; SARIF gets the
      honesty channels via `invocations`/`notifications`. Decided (Umang,
      2026-08-19): always warn on stderr +
      always carry parse health/skip counts in SARIF notifications; exit code
      stays 0 unless a new `--strict` flag is passed, so existing pipelines
      keep passing while the honesty is visible. This is THE one schema change, so the three decisions
      blocked on "changes every finding type at once" land inside it, not
      separately: (a) records carry a `SourceSpan` (retires the ~84
      hand-threaded `(sourcePath, StartLine, StartColumn)` triples), (b)
      per-instance confidence (fixed per rule type today; matters for a
      handful of rules), (c) engine-version sensitivity as a modeled field
      (today rationale prose; only `QueryAntiPatternScanner` and
      `ScalarUdfInfo.EngineIsInlineable` branch on `CompatibilityLevel` at
      runtime) — fold in `TypePairMatrix`'s unread `ServerVersion` stamp
      (16.0.4236.2/compat 160, zero consumers, so older/newer targets
      silently get that build's verdicts at full confidence) and a
      `ScanReport.CurrentSchemaVersion` round-trip test so a finding-shape
      change forces a version bump.
- [ ] **Phase 4 — terminology rename.** ~384 "corpus" + ~609 "oracle" in
      `src/`, including namespaces (`SilentScan.Core.Corpus`,
      `SilentScan.Verify.Oracle`), public types, the `scan-corpus-live` verb,
      fixture dirs, and docs. Mechanical but touches the CLI contract —
      ride it on Phase 3's churn, pick replacement terms with Umang first.

---

## Settled (do not re-propose)

* **Confidence stays.** Load-bearing in the `--confidence` filter, the SARIF
  tier, and `DynamicSqlPipeline`'s downgrade of findings that rest on an
  assumption.
* **Source-context classification** (migration script vs hot-path module) —
  dropped. No signal precise enough to avoid suppressing real findings.
* **The incumbent survey is closed.** `detection-reference.md` §7.9–7.11.
* **Killed candidates stay killed.** Each has its measurement in
  `detection-reference.md` Appendix 9; re-read it before re-proposing one.
* **Redundant CAST/CONVERT does not rescue sargability — do not re-propose
  suppressing it.** Oracle-confirmed: a CAST to a type identical to the
  wrapped column's own still produces a Table Scan, not a Seek.
  `detection-reference.md`, "Sargability and index eligibility."
* **"One binder" shipped.** `FromScopeResolver`/`CteResolver`/`BaseColumnResolver`
  are now the only name-resolution path predicates go through;
  `DirectBaseTableResolver` (the second, independent bypass) is deleted.
  `SelectIntoColumnResolver` was deliberately excluded, not missed — it runs
  at catalog-build time, before Lineage exists, and CLAUDE.md's pass-ordering
  rule ("catalog building resolves against tables only, never views... because
  view resolution is Lineage's job") forbids folding a catalog-time resolver
  into a Lineage-time binder. Do not re-propose merging it in.

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
