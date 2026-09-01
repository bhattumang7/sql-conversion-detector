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

- [ ] **Lower-confidence/niche backlog from the 2026-08-22 gap survey — one
      line each, group before scoping.** These didn't clear the bar for a
      full write-up above (medium/low survey confidence, a narrower feature
      area, or "verify it isn't already covered" rather than a clean new
      gap) but are real enough not to drop silently. Each still needs its
      own oracle confirmation before design.
      1. Verify the shipped predicate-survival algebra already folds `LIKE`
        patterns into its interval model, and already flattens nested
        AND/OR trees (not just direct conjuncts/disjuncts) before treating
        those as closed — may already be covered by the recent commit.
        **FINDING:** split verdict. `PredicateSurvivalAnalyzer.Flatten`
        already recurses through nested same-type AND/OR trees and unwraps
        parens — that half is already correct, not a gap. But the analyzer
        has no `LIKE`/`BooleanLikeExpression` handling at all — patterns
        are not folded into the interval model. Real, narrower gap: LIKE
        folding only.
      2. `ScalarUdfInlineabilityScanner`: the survey claims it covers only
        about half the engine's real inlineability checks; beyond the two
        gaps already written up above (compat-level gate, re-eval-count
        threshold), enumerate the remaining checks against the scanner's
        own visitor one at a time rather than acting on the vague aggregate
        figure. **FINDING:** attempted, inconclusive. `OBJECTPROPERTYEX(id,
        'IsInlineable')` returned `NULL` for both a trivial function and a
        non-inlineable one (using `ERROR_NUMBER()`) on SQL Server 2022 —
        not a usable oracle signal as tried. Needs a different verification
        method (e.g. checking actual plan inlining) before scoping.
      3. `VerdictClassifier.IsOutOfModelCategory` returns `Unknown` (never an
        actionable finding) for XML/JSON/UDT/legacy-LOB comparisons where
        the engine's own comparability gate hard-rejects the comparison —
        check this against the oracle-probed type matrix; if real, these
        should reclassify as `OperandClash`, not stay `Unknown`.
        **FINDING: confirmed real** for XML and spatial (UDT). Oracle:
        `CAST(x AS XML) = CAST(x AS XML)` → Msg 305; `sql_variant = xml`
        assignment → Msg 206 (operand type clash); `geometry = geometry`
        → Msg 403. All are hard compile-time rejects, not "unknown."
        `VerdictClassifier.cs` lines 97-100 lump these into `Unknown`.
        JSON and legacy-LOB (text/ntext/image) categories not individually
        oracle-tested — check separately before folding them into the same
        reclassification.
      4. New family: an assignment (`SET`/`INSERT`/`UPDATE`) whose source
        type cannot legally implicit-convert to the target at all
        (encryption-state mismatch, illegal collation coercion, legacy-LOB
        ineligibility) is a compile-time reject, a stronger and distinct
        claim from `WriteLossFinding`'s "compiles but silently loses data".
        **FINDING: collation-coercion leg is false as an assignment claim.**
        Local variables don't support a `DECLARE ... COLLATE` clause at all
        on this engine build (`Msg 156`) — that's not a code-side gap to
        chase, it's simply not legal syntax. Tested instead with two real
        columns carrying different collations: a cross-collation column
        assignment (`UPDATE b SET b.v = a.v`) is silently allowed (implicit
        conversion to the target's collation, no error) — so assignment
        itself is *not* a hard reject for collation. The genuine hard
        reject (`Msg 468`, unresolvable collation conflict) only fires on a
        *comparison* predicate (`a.v = b.v` in a JOIN/WHERE), which is
        already `CollationConflictRuleId`'s territory, not a new assignment
        family. `sql_variant = xml` (item 3) remains the one confirmed
        "cannot implicit-convert at all" case; encryption-state and
        legacy-LOB legs still unverified.
      5. `ProcCallArgumentMismatchRuleId`: the reverse direction — a
        callee's `OUTPUT` parameter's real assigned value marshalled back
        into the caller's receiving variable — is currently uncovered;
        mechanism needs pinning down before scoping. **FINDING: confirmed
        real and silent.** A proc's `varchar(100) OUTPUT` param assigning a
        50-char value, received into a caller's `varchar(3)` variable,
        truncates to `'xxx'` with no error and no warning. Same
        silent-data-loss shape as the shipped forward-direction rule.
      6. New family: online DDL blocked by column type. `ALTER COLUMN`/
        `ALTER TABLE`/`ALTER INDEX ... REBUILD`/`DROP INDEX` with `ONLINE`,
        and a whole-table online rebuild, are all documented to reject a
        legacy-LOB/CLR-incompatible column type shape — one consolidated
        rule, not four/five separate ones. **FINDING: confirmed real** for
        the two forms tested. `ALTER TABLE ... REBUILD WITH (ONLINE=ON)`
        and `ALTER INDEX ALL ... REBUILD WITH (ONLINE=ON)` both reject a
        table with an `ntext` column, same catalog-decidable error message.
        `ALTER COLUMN`/`DROP INDEX ONLINE` forms not individually tested.
      7. New family: partition/filegroup DDL alignment siblings to the
        shipped `ALTER TABLE SWITCH` family — partition-`REBUILD` alignment
        mismatch, `DROP` against a non-updateable (read-only/offline)
        filegroup, FILESTREAM data-space compatibility mismatch, a
        partition scheme's columns disagreeing with the partitioning
        columns, and a compile-time-foldable partition number exceeding the
        engine's 14999 ceiling. **FINDING:** not tested this pass.
      8. `CREATE TRIGGER` on a FILESTREAM-backed table failing at DDL time.
        **FINDING:** not tested this pass — needs FILESTREAM enabled on a
        throwaway database, out of scope for the VALUES-only probes used.
      9. `NonPersistedComputedColumnRuleId`/`TryCastComputedColumnPredicateRuleId`
        sibling: the direct DDL-time hard failure when an indexed view or
        indexed computed column references a nondeterministic expression —
        check whether this boundary is already caught downstream.
        **FINDING: confirmed real, but likely unreachable as a new
        rule.** Oracle: `CREATE UNIQUE CLUSTERED INDEX` on a schemabound
        view selecting `NEWID()` → "yields nondeterministic results";
        `CREATE INDEX` on a computed column using `NEWID()` → "cannot be
        used in an index... because it is non-deterministic." Same shape
        as the temporal gap closed as unreachable in a recent commit — SQL
        Server refuses to *create* the index at all, so a scanner reading
        only the live catalog would never observe the bad state.
      10. `UNPIVOT` mixing source columns with incompatible types
        (`sys.columns`-decidable). **FINDING: confirmed real, and
        stricter than the bullet implies.** `int` vs `xml` errors as
        expected (Msg 8167), but so does `int` vs `bigint` — UNPIVOT
        requires *exact* type match, not just implicit-convertibility.
        Decidable directly from `sys.columns` type equality; scope the
        rule as exact-type-mismatch, not "incompatible types."
      11. Memory-optimized (Hekaton) natively compiled module restrictions
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
        shape. **FINDING:** not tested this pass — needs a
        MEMORY_OPTIMIZED_DATA filegroup set up on a throwaway database.
      12. `WITH SCHEMABINDING` referencing an alias user type (or an invalid
        parsed type name) is a documented restriction. **FINDING: confirmed
        real.** `CREATE FUNCTION ... (@x dbo.MyAliasType) RETURNS int WITH
        SCHEMABINDING` fails at create time (`Msg 2792`: "Cannot specify a
        sql CLR type in a Schema-bound object..." — the message's "CLR
        type" wording is misleading, the type here was a plain
        `CREATE TYPE ... FROM int` alias, not CLR). Since the function
        never comes into existence, there's no live-catalog "referenced by
        a schemabound object" state to detect after the fact — the value
        here is a param/return-type check on schemabound `CREATE
        FUNCTION`/`VIEW`/`TRIGGER` statements themselves, not a dangling-
        reference scan. The "invalid parsed type name" half wasn't
        separately tested.
      13. Full-text index DDL validation (unsupported column type, invalid
        language id, nondeterministic computed column, >1024 indexed
        columns) — real but needs new full-text-index modeling in the
        catalog builder that doesn't exist today. **FINDING:** not tested
        this pass, despite an FTS-enabled container being available
        locally — deprioritized given the catalog-builder prerequisite
        called out in the bullet itself.
      14. Always Encrypted per-type restrictions beyond the comparison/index
        family already written up — a column type the engine's own
        encryption-support rules reject outright. **FINDING:** not tested
        this pass.
      15. `TemporalTableHistoryIndexGapRuleId`-family sibling: current/history
        table schema divergence (type/precision/scale/collation/encryption)
        blocking temporal validation, distinct from the column-mapping gap
        already written up. **FINDING: confirmed real.** A current table
        with `v varchar(10)` and a history table with `v varchar(5)`
        rejects `ALTER TABLE ... SET (SYSTEM_VERSIONING = ON (...))` with a
        precise, catalog-decidable message naming both types. Only the
        length-divergence leg was tested; precision/scale/collation/
        encryption divergence not individually probed but same mechanism
        is very likely.
      16. Sparse column type/compression restrictions (`sys.columns.is_sparse`
        plus table compression state) — allow-list is version-dependent.
        **FINDING: confirmed real for the type-allow-list half.** A sparse
        `ntext` column and a sparse `geometry` (UDT) column both reject at
        create time with `Msg 1731` ("cannot be of the follow[ing types]").
        A sparse `xml` column, by contrast, is **allowed** — don't assume
        XML belongs on the disallowed list. The `NOT NULL` + `SPARSE`
        combination wasn't cleanly isolated (test hit a DDL syntax issue,
        not an engine rejection) — retest before relying on that leg.
        Compression-state interaction not tested.
      17. Legacy LOB type (`text`/`ntext`/`image`) paired with a surrogate-
        aware or UTF-8 collation. **FINDING:** not tested this pass.
      18. New family: `STRING_SPLIT`/`REGEXP_MATCHES`-style string TVF
        argument-type and MAX-width validation, and `STRING_SPLIT`'s
        3-argument ordinality form being version-gated — fold together
        with the shipped-candidate `REGEXP_*` MAX-argument family above
        into one string-TVF argument-validation rule. **FINDING: the
        version-gating half needs a real gate check, not just a compat-
        level flip.** The 3-arg ordinality form works fine at database
        compatibility level 160 (SQL 2022 default) — expected, since it
        shipped in SQL Server 2022. It *also* still worked after dropping
        compatibility level to 150 on the same SQL 2022 engine, meaning
        compat level alone doesn't gate it here (the gate is almost
        certainly the engine's own version, not the database's compat
        level) — don't scope a compat-level check for this, verify against
        actual major version instead. Argument-type/MAX-width validation
        legs not tested.
      19. Semantic Search TVFs (`SEMANTICKEYPHRASETABLE` etc.) requiring a
        qualifying full-text semantic index — legacy/rarely used feature.
        **FINDING:** not tested this pass.
      20. New family (SQL Server 2025): `JSON_VALUE(...RETURNING...)`/
        `JSON_CONTAINS` exact-match predicate shapes eligible for a JSON
        index rewrite — the JSON-index sargability counterpart to the
        shipped `IndexCoverageKeyLookupProneIndexRuleId` family; needs an
        oracle matrix for what "exact match" precisely means on a brand-new
        feature. **FINDING:** not tested this pass, despite the 2025
        container being available locally.
      21. `CollationConflictRuleId`: confirm `GREATEST`/`LEAST` (2022+)
        arguments are actually walked by the existing collation-conflict
        predicate walker — genuinely incompatible collations there should
        already report but may not. **FINDING: engine side confirmed
        real** — `GREATEST('a' COLLATE Latin1_General_CI_AS, 'b' COLLATE
        French_CI_AS)` errors with Msg 468 (collation conflict). Whether
        the scanner's own predicate walker visits `GREATEST`/`LEAST` calls
        was not checked (source-code side of this item still open).
      22. Broaden the float-non-determinism family (aggregate argument,
        already written up) to float-typed arithmetic operands generally
        and float constants in precision-sensitive expressions — likely one
        rule, not three. **FINDING:** not tested this pass.
      23. `REVERT WITH COOKIE = @x` requiring `@x` to be a fixed-size
        `varbinary` matching the engine's cookie type/size is decidable
        from the variable's own declaration. **FINDING: confirmed real.**
        A `varbinary(10)` cookie is rejected with Msg 15533 ("Invalid data
        type is supplied in the 'Revert' statement"); the engine requires
        the fixed `varbinary(100)` shape produced by `CREATE USER ...
        WITH... COOKIE INTO`. Decidable from the variable's declared type.
      24. Broaden the `LAG`/`LEAD`/`PERCENTILE_*` constant-argument-validation
        family (already written up) to cover any compile-time-constant
        percent-like argument outside the inclusive 0-100 range (e.g.
        `TABLESAMPLE PERCENT`) — same mechanism, one family. **FINDING:
        confirmed real** for the `TABLESAMPLE PERCENT` case — a literal
        `150 PERCENT` is rejected with Msg 476 ("must be between 0 and
        100"). Supports folding it into the same family.
      25. `FOR XML` forbidden option combinations (e.g. `EXPLICIT` with inline
        XSD) — decidable purely from the statement's own option list, no
        catalog access needed. **FINDING: real, but as an unimplemented
        feature, not a documented "forbidden combination."** `FOR XML
        EXPLICIT, XMLSCHEMA` fails with "'Inline XSD for FOR XML EXPLICIT'
        is not yet implemented" — still a real, decidable compile-time
        reject, just frame the rationale accordingly.
      26. New `SecurityFindingKind`: `sp_invoke_external_rest_endpoint` is a
        real outbound-network call surface distinct from the shipped
        hardcoded-IP-address finding. **FINDING:** not tested this pass.
      27. `sp_execute_external_script`'s `WITH RESULT SETS`-style column
        declaration reusing a name, omitting a required type binding, or
        declaring a rejected type. **FINDING:** not tested this pass.
      28. `OPENJSON WITH` schema projecting a native `json`-typed column
        while the enabling feature switch is off. **FINDING:** not tested
        this pass.
      29. `VECTOR_DISTANCE`-family calls with a large-object-typed operand
        (SQL Server 2025 vector feature). **FINDING:** not tested this
        pass, despite the 2025 container being available locally.
      30. `OPENXML`/`OPENROWSET WITH` schema resolving a column to a type the
        engine's fixed type gate rejects (`sql_variant`/spatial/legacy-LOB)
        — one rule covering both clauses. **FINDING:** not tested this
        pass.
      31. `AnsiPaddingMismatchRuleId`: the shipped rule only covers `LIKE`
        trailing-whitespace matching; the same trim/no-trim boundary
        reportedly also affects join matching, equality, and persisted-
        expression results more broadly. **FINDING: as tested, false —
        general equality trailing-space trimming is a universal ANSI SQL
        behavior, not an `ANSI_PADDING`-dependent boundary.** `'ab' =
        'ab   '` returns true unconditionally (this is the standard
        SQL trailing-space comparison rule, independent of any session
        setting) — that's not a gap, it's baseline engine behavior every
        query already relies on. A `varchar` column with `ANSI_PADDING
        OFF` also did not trim trailing spaces on storage (`DATALENGTH`
        stayed 5), which is expected too (`ANSI_PADDING` only affects
        fixed-length `char`/`binary` padding, not `varchar` storage). I
        did not find a case where join/equality results actually differ
        by `ANSI_PADDING` state — this bullet's premise looks confused
        with the storage-vs-comparison distinction. Recommend dropping
        unless a more specific persisted-expression scenario is proposed
        and confirmed.
      32. `EXECUTE AT DATA_SOURCE` (elastic query) with a large-object-typed
        parameter. **FINDING:** not tested this pass.
      33. Informational, database-configuration tier: an active
        `sys.plan_guides` row whose hints alter optimization/parameterization
        for in-scope application SQL. **FINDING:** not tested this pass.
      34. External file-format/data-export partition column type restrictions
        (PolyBase/CETAS external-table column-type and virtual-column
        allow-lists; data-export partition column resolving to a large
        object or unsupported type) — real but niche. **FINDING:** not
        tested this pass.
      35. A statically-known boolean element inside a JSON literal converted
        to the native `VECTOR` type (SQL Server 2025 feature, narrow).
        **FINDING:** not tested this pass.
      36. A full-text predicate (`CONTAINS`/`FREETEXT`) used inside an
        aggregate/`GROUP BY` scope the engine rejects. **FINDING:** not
        tested this pass.
      37. A window `PARTITION BY` expression resolving to a type SQL Server
        cannot compare for partitioning (LOB/XML/spatial). **FINDING:
        confirmed real**, same comparability gate as items 3 and 10.
        `ROW_NUMBER() OVER (PARTITION BY x ORDER BY ...)` with `x` typed
        `xml` fails with the same Msg 305 as a direct XML comparison —
        one shared comparability-gate rule can cover items 3, 10, and 37,
        not three separate ones.
      38. New family: `CHANGE_TRACKING` restrictions — `ALTER TABLE ...
        ENABLE CHANGE_TRACKING` against a table carrying an Always Encrypted
        column, and change tracking already enabled on a table carrying a
        legacy LOB column (matches a real engine-emitted warning).
        **FINDING:** not tested this pass.
      39. `ProcCallArgumentMismatchRuleId` sibling: a streaming/inline TVF's
        own parameter boundary needing an implicit conversion, the same
        silent-marshalling family applied to a different call-site kind.
        **FINDING: confirmed real and silent.** An inline TVF declared
        `(@p varchar(3))` called as `dbo.probe_itvf('hello')` returns
        `'hel'` — the 5-char literal is silently truncated to the
        parameter's declared width with no error, same shape as the
        shipped forward-direction proc-call rule and as item 5's OUTPUT
        finding.
      40. `SessionDateSettingRuleId(DateFormat)` may be scoped too narrowly:
        the shipped rule only fires when the module's own body contains an
        explicit `SET DATEFORMAT`, but an ambiguous string-to-date
        conversion is reportedly session-format-dependent under
        compatibility level > 99 even with no `SET DATEFORMAT` present in
        the module — a real under-detection gap if confirmed, not just a
        new rule. **FINDING: confirmed real, and significant.** With
        *no* `SET DATEFORMAT` anywhere and `us_english` as the session
        language, `CAST('02/03/2024' AS date)` parses as **2024-02-03**
        (Feb 3). After only `SET LANGUAGE British` (still no
        `SET DATEFORMAT` statement anywhere), the identical literal parses
        as **2024-03-02** (Mar 2) — same string, same module text,
        different result, driven entirely by the session's default
        language/dateformat. The shipped rule's premise — that the risk
        only exists when the module body contains an explicit
        `SET DATEFORMAT` — is too narrow; any ambiguous `mm/dd`-vs-`dd/mm`
        date literal is at risk regardless of whether `SET DATEFORMAT`
        appears anywhere.
      41. Fold into the bounded-string-builtins family already written up:
        `STRING_AGG`'s result type is capped at `VARCHAR(8000)`/
        `NVARCHAR(4000)` when none of its operands are MAX-typed, regardless
        of row count — a structural type-level fact, not row-count-dependent.
        **FINDING: confirmed real, both legs.** `sys.dm_exec_describe_first_result_set`
        against a `STRING_AGG(CAST(x AS varchar(10)), ',')` call with no
        MAX-typed operand reports the result column as `varchar(8000)`
        regardless of input row count. The `nvarchar(10)` operand variant
        reports `max_length = 8000` too — that's bytes, which is exactly
        `nvarchar(4000)` (2 bytes/char), confirming the `NVARCHAR(4000)`
        cap as claimed.
      42. New family: `NVARCHAR` to a UTF-8-collation `VARCHAR` conversion (and
        the reverse) can expand/contract byte length past the declared
        target's 8000-byte cap — distinct failure mode from
        `WriteLossUnicodeReplacementRuleId` (byte-length overflow, not
        codepage `?` replacement); needs an oracle pass on exact truncation
        behavior. **FINDING: confirmed real.** 4000 `nvarchar` characters
        of a 3-byte-in-UTF-8 CJK character (12,000 UTF-8 bytes) assigned
        into a `varchar(8000) COLLATE Latin1_General_100_CI_AS_SC_UTF8`
        column raises "String or binary data would be truncated" — a hard
        error under default session settings, distinct in kind from the
        silent `?`-replacement codepage-loss rule. Whether it's a hard
        error or a silent truncation depends on `ANSI_WARNINGS`/statement
        context — worth pinning down both modes before scoping.
      43. Explicit `INSERT`/`UPDATE`/`MERGE` assignment to a SQL Graph node/
        edge table's own `$node_id`/`$edge_id` system column. **FINDING:
        confirmed real.** `$node_id` is backed by hidden system columns
        that reject direct manipulation: supplying an explicit value via
        the normal `INSERT` column list fails ("Cannot insert the value
        NULL into column 'graph_id_...'"), and `UPDATE ... SET $node_id =
        $node_id` fails with "cannot be modified because it is either a
        computed column..." — the column is effectively immutable/
        system-managed, confirming the restriction.
      44. Heavier-lift candidate: a joined table catalog-provably contributing
        nothing (no projected columns/predicates/grouping/ordering, and
        FK/uniqueness/nullability prove it can't change multiplicity or
        null-extension) — real simplification finding, but the conservative
        multiplicity/null-extension proof is substantial engineering, not a
        quick win. **FINDING:** not tested this pass; this one is a design
        question more than an oracle question.
      45. `QueryAntiPatternLinkedServerOrCrossDatabaseReferenceRuleId`:
        sharpen the existing "close to a guess" framing — a linked-server/
        remote-query source reportedly gets a fixed exactly-1-row
        cardinality estimate, an oracle-confirmable mechanical fact rather
        than a vague warning, the same precision upgrade already done for
        the table-variable-low-compat-estimate rule. **FINDING:** not
        tested this pass — needs a linked server configured, out of scope
        for the VALUES-only probes used.
      46. `CheckConstraintNullNotHandledRuleId`-family sibling: a DML
        statement against a `WITH CHECK OPTION` view whose inserted/updated
        values are provably contradicted by the view's own predicate —
        confidence is only medium/unverified; likely detectable for literal
        values only in practice. **FINDING: engine mechanism confirmed,
        static detectability still open.** `INSERT INTO viewName (id, v)
        VALUES (1, 5)` against a view defined `WHERE v > 10 WITH CHECK
        OPTION` fails at runtime with the documented CHECK OPTION message
        — the enforcement mechanism is real and well-known, nothing new
        there. What's still unverified is the actual ask: statically
        proving a *literal* `VALUES` row is contradicted by the view's own
        predicate before running it. That's a design/implementation
        question about the scanner's constant-folding reach, not an
        oracle question — the oracle side of this item is done.
      47. `DeprecatedSyntaxDeprecatedSetRowcountRuleId` is scoped too
        narrowly: it only warns `SET ROWCOUNT` will stop being honored by
        DML in a future release, but a nonzero `SET ROWCOUNT` left active
        silently limits rows affected/returned by every subsequent
        statement right now — a present-tense correctness risk, not just a
        future-deprecation one. **FINDING: confirmed real.** `SET ROWCOUNT
        1` before a 3-row insert into a table variable makes a subsequent
        `SELECT COUNT(*)` return `1`, not `3` — a live correctness risk
        today, independent of the future-deprecation angle.
      48. `DBCC RULE ON/OFF` toggles the same legacy `CREATE RULE`/
        `sp_bindrule` mechanism already flagged elsewhere as deprecated —
        using it at all is the same decidable deprecated-syntax fact.
        **FINDING: false — drop this item.** `DBCC RULE` is not a real
        DBCC statement on either local instance: `DBCC HELP('RULE')`
        returns "No help available for DBCC statement 'RULE'", and the
        syntax itself doesn't parse. The premise is wrong.
      49. Ledger tables restrict which `ALTER COLUMN` shapes are legal
        (`sys.tables.is_ledger_on` plus before/after column shape) — narrow
        feature. **FINDING: not confirmed — appears less restrictive than
        claimed on this engine build.** Tested both an append-only ledger
        table and an updatable (`SYSTEM_VERSIONING = ON`) ledger table: in
        both cases, `ALTER TABLE ... ALTER COLUMN` (widening *and*
        narrowing) and `ALTER TABLE ... DROP COLUMN` all succeeded with no
        error. If a real restriction exists it wasn't triggered by these
        two straightforward shapes — needs a more specific scenario (e.g.
        a column already referenced by ledger history, or a specific type
        change) before treating this as a confirmed gap. As tested: no
        evidence of a restriction.
      50. `DanglingObjectReferenceRuleId` sibling: a CLR aggregate whose
        catalog-registered `Terminate`/`Accumulate` method can no longer be
        resolved after `ALTER ASSEMBLY` fails only on first invocation —
        same deferred-resolution shape, but CLR aggregates are rare.
        **FINDING:** not tested this pass.
      51. `CREATE`/`ALTER XML SCHEMA COLLECTION` binding a column to a
        disallowed scalar type. **FINDING:** attempted, but the probe was
        misdirected (tested `SPARSE` + typed-`xml` column, not an XSD
        type-binding restriction) — genuinely not tested this pass.
        Side-result worth keeping: a sparse `xml` column (typed or
        untyped) is allowed — don't add XML to item 16's disallowed-type
        list.
      52. New consolidated rule: CLR UDT catalog-metadata validity — two UDT
        signatures treated as interchangeable when they aren't, a
        referenced UDT method that can't be resolved, an incompatible CLR
        array conversion, a UDT participating in an operator its metadata
        doesn't support. Hand-authored CLR UDTs beyond the built-in spatial
        types are rare, so low real-world hit rate. **FINDING:** not
        tested this pass.
      53. `sp_cursoropen`/`sp_cursorexecute` called with a literal scroll-
        option bitmask or `paramdef` shape the engine rejects — usually
        client-driver-generated rather than hand-authored, low value.
        **FINDING:** not tested this pass.
      54. `BACKUP`/`RESTORE` and `CREATE DATABASE` forbidden option
        combinations, decidable purely from the statement's own option
        list — DBA-maintenance-script scope, not typical application SQL.
        **FINDING: confirmed real for one concrete combo.**
        `BACKUP DATABASE ... WITH DIFFERENTIAL, COPY_ONLY` always fails —
        a copy-only full backup never registers as a differential base, so
        any later differential can never find "a current database backup"
        to diff against (`Msg 3035`). This is a genuine, always-fails,
        decidable-from-the-statement's-own-option-list combination, though
        the actual error only surfaces as the generic "no current backup"
        message rather than a dedicated "COPY_ONLY+DIFFERENTIAL is
        invalid" message — still confirms the practical restriction.
        `CREATE DATABASE` combos not tested.
      55. `IndexDesignRuleId` sibling: an index definition repeating the same
        column across its partition/key/include/order-by lists. **FINDING:
        confirmed real, but likely unreachable as a live-catalog finding**
        — same shape as item 9. `CREATE INDEX ... (a) INCLUDE (a)` and
        `CREATE INDEX ... (a, a)` both reject at DDL time with `Msg 1909`
        ("duplicate column names in index"), so such an index can never
        exist in a live catalog for a scanner to find. Partition-column
        and order-by-column repetition (columnstore) weren't tested and
        may behave differently — worth checking those specific forms
        before writing this off entirely.
      56. PolyBase/Hadoop external-table column-type and virtual-column
        restrictions — mainstream on-prem feature but low adoption.
        **FINDING:** not tested this pass.
      57. A typed XML variable resolving to a different/missing schema
        collection than its type metadata records — rare in normal
        authoring. **FINDING:** not tested this pass.
      58. New consolidated family, sibling to `DanglingObjectReferenceRuleId`:
        an object protected from `DROP` by dependents or protection state —
        `DROP ROLE` targeting a protected fixed role while protection is
        active, `DROP SCHEMA` on a non-empty schema, `DROP EXTERNAL DATA
        SOURCE`/`DROP EXTERNAL FILE FORMAT` blocked by a dependent external
        table/stream. **FINDING:** not tested this pass.

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
