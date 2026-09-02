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
        legacy-LOB legs still unverified. **Shipped, broadened beyond the
        original leg:** oracle probing the `sql_variant`/`xml` pairing
        further found the true restriction is bigger than "these two types
        clash" - a `sql_variant` source can never be read directly into any
        differently-typed target (Msg 257 for ordinary types, Msg 206 for
        xml specifically), and an `xml` target only accepts an implicit
        assignment from another `xml` value or a character/binary-family
        source, never anything else (Msg 206). Shipped as
        `RestrictedImplicitAssignmentRuleId` covering both general
        restrictions for local variable and parameter assignments.
        Encryption-state and legacy-LOB legs remain open.

      7. New family: partition/filegroup DDL alignment siblings to the
        shipped `ALTER TABLE SWITCH` family — partition-`REBUILD` alignment
        mismatch, `DROP` against a non-updateable (read-only/offline)
        filegroup, FILESTREAM data-space compatibility mismatch, a
        partition scheme's columns disagreeing with the partitioning
        columns, and a compile-time-foldable partition number exceeding the
        engine's ceiling. **FINDING:** partial. The ceiling is 15000, not
        14999 as originally guessed (oracle-confirmed, see
        `detection-reference.md`) — partition number 15000 itself is valid,
        15001 is rejected (Msg 7722, "range from 1 to 15000"). The
        read-only-filegroup-drop probe was inconclusive: `DROP` on a
        read-only-but-still-present filegroup fails with Msg 5042 ("not
        empty"), which fires for any filegroup still carrying a data file
        regardless of read-only state — doesn't isolate a read-only-specific
        rejection. Remaining legs (partition-REBUILD alignment mismatch,
        FILESTREAM data-space mismatch, partition-scheme/partitioning-column
        disagreement) not tested this pass.

      10. Memory-optimized (Hekaton) natively compiled module restrictions
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
        shape. **FINDING:** not tested this pass — not infra-blocked
        (confirmed a `MEMORY_OPTIMIZED_DATA` filegroup creates cleanly with
        plain T-SQL on the local container, no special setup needed),
        simply not attempted yet.

      12. Full-text index DDL validation (unsupported column type, invalid
        language id, nondeterministic computed column, >1024 indexed
        columns) — real but needs new full-text-index modeling in the
        catalog builder that doesn't exist today. **FINDING:** not tested
        this pass, despite full-text capability being available directly on
        `mssql-silentscan-sql` (`IsFullTextInstalled` = 1; the separate
        `silentscan-mssql-fts` container CLAUDE.md references no longer
        exists) — deprioritized given the catalog-builder prerequisite
        called out in the bullet itself.

      13. Always Encrypted per-type restrictions beyond the comparison/index
        family already written up — a column type the engine's own
        encryption-support rules reject outright. **FINDING:** not tested
        this pass.

      16. New family: `STRING_SPLIT`/`REGEXP_MATCHES`-style string TVF
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

      17. Semantic Search TVFs (`SEMANTICKEYPHRASETABLE` etc.) requiring a
        qualifying full-text semantic index — legacy/rarely used feature.
        **FINDING:** not tested this pass.

      18. New family (SQL Server 2025): `JSON_VALUE(...RETURNING...)`/
        `JSON_CONTAINS` exact-match predicate shapes eligible for a JSON
        index rewrite — the JSON-index sargability counterpart to the
        shipped `IndexCoverageKeyLookupProneIndexRuleId` family; needs an
        oracle matrix for what "exact match" precisely means on a brand-new
        feature. **FINDING:** not tested this pass, despite the 2025
        container being available locally.

      20. Broaden the float-non-determinism family (aggregate argument,
        already written up) to float-typed arithmetic operands generally
        and float constants in precision-sensitive expressions — likely one
        rule, not three. **FINDING:** not tested this pass.

      24. New `SecurityFindingKind`: `sp_invoke_external_rest_endpoint` is a
        real outbound-network call surface distinct from the shipped
        hardcoded-IP-address finding. **FINDING:** not tested this pass.

      25. `sp_execute_external_script`'s `WITH RESULT SETS`-style column
        declaration reusing a name, omitting a required type binding, or
        declaring a rejected type. **FINDING:** not tested this pass.

      26. `OPENJSON WITH` schema projecting a native `json`-typed column
        while the enabling feature switch is off. **FINDING:** not tested
        this pass.

      27. `VECTOR_DISTANCE`-family calls with a large-object-typed operand
        (SQL Server 2025 vector feature). **FINDING:** not tested this
        pass, despite the 2025 container being available locally.

      28. `OPENXML`/`OPENROWSET WITH` schema resolving a column to a type the
        engine's fixed type gate rejects (`sql_variant`/spatial/legacy-LOB)
        — one rule covering both clauses. **FINDING:** not tested this
        pass.

      29. `EXECUTE AT DATA_SOURCE` (elastic query) with a large-object-typed
        parameter. **FINDING:** not tested this pass.

      30. Informational, database-configuration tier: an active
        `sys.plan_guides` row whose hints alter optimization/parameterization
        for in-scope application SQL. **FINDING:** not tested this pass.

      31. External file-format/data-export partition column type restrictions
        (PolyBase/CETAS external-table column-type and virtual-column
        allow-lists; data-export partition column resolving to a large
        object or unsupported type) — real but niche. **FINDING:** not
        tested this pass.

      32. A statically-known boolean element inside a JSON literal converted
        to the native `VECTOR` type (SQL Server 2025 feature, narrow).
        **FINDING:** not tested this pass.

      33. A full-text predicate (`CONTAINS`/`FREETEXT`) used inside an
        aggregate/`GROUP BY` scope the engine rejects. **FINDING:** not
        tested this pass.

      35. New family: `CHANGE_TRACKING` restrictions — `ALTER TABLE ...
        ENABLE CHANGE_TRACKING` against a table carrying an Always Encrypted
        column, and change tracking already enabled on a table carrying a
        legacy LOB column (matches a real engine-emitted warning).
        **FINDING:** not tested this pass.

      36. `ProcCallArgumentMismatchRuleId` sibling: a streaming/inline TVF's
        own parameter boundary needing an implicit conversion, the same
        silent-marshalling family applied to a different call-site kind.
        **FINDING: confirmed real and silent.** An inline TVF declared
        `(@p varchar(3))` called as `dbo.probe_itvf('hello')` returns
        `'hel'` — the 5-char literal is silently truncated to the
        parameter's declared width with no error, same shape as the
        shipped forward-direction proc-call rule and as item 5's OUTPUT
        finding.

      37. `SessionDateSettingRuleId(DateFormat)` may be scoped too narrowly:
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

      38. Fold into the bounded-string-builtins family already written up:
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

      39. New family: `NVARCHAR` to a UTF-8-collation `VARCHAR` conversion (and
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

      41. Heavier-lift candidate: a joined table catalog-provably contributing
        nothing (no projected columns/predicates/grouping/ordering, and
        FK/uniqueness/nullability prove it can't change multiplicity or
        null-extension) — real simplification finding, but the conservative
        multiplicity/null-extension proof is substantial engineering, not a
        quick win. **FINDING:** not tested this pass; this one is a design
        question more than an oracle question.

      42. `QueryAntiPatternLinkedServerOrCrossDatabaseReferenceRuleId`:
        sharpen the existing "close to a guess" framing — a linked-server/
        remote-query source reportedly gets a fixed exactly-1-row
        cardinality estimate, an oracle-confirmable mechanical fact rather
        than a vague warning, the same precision upgrade already done for
        the table-variable-low-compat-estimate rule. **FINDING:** not
        tested this pass. A self-referencing linked server
        (`SILENTSCAN_LOOPBACK`) is now committed/available on the 2022 local
        instance (`docs/local-dev.md`) for this — the infra gap is closed,
        oracle confirmation of the cardinality-estimate claim itself is
        still open.

      43. `CheckConstraintNullNotHandledRuleId`-family sibling: a DML
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

      45. `DanglingObjectReferenceRuleId` sibling: a CLR aggregate whose
        catalog-registered `Terminate`/`Accumulate` method can no longer be
        resolved after `ALTER ASSEMBLY` fails only on first invocation —
        same deferred-resolution shape, but CLR aggregates are rare.
        **FINDING:** not tested this pass.

      46. `CREATE`/`ALTER XML SCHEMA COLLECTION` binding a column to a
        disallowed scalar type. **FINDING:** attempted, but the probe was
        misdirected (tested `SPARSE` + typed-`xml` column, not an XSD
        type-binding restriction) — genuinely not tested this pass.
        Side-result worth keeping: a sparse `xml` column (typed or
        untyped) is allowed — don't add XML to item 14's disallowed-type
        list.

      47. New consolidated rule: CLR UDT catalog-metadata validity — two UDT
        signatures treated as interchangeable when they aren't, a
        referenced UDT method that can't be resolved, an incompatible CLR
        array conversion, a UDT participating in an operator its metadata
        doesn't support. Hand-authored CLR UDTs beyond the built-in spatial
        types are rare, so low real-world hit rate. **FINDING:** not
        tested this pass.

      48. `sp_cursoropen`/`sp_cursorexecute` called with a literal scroll-
        option bitmask or `paramdef` shape the engine rejects — usually
        client-driver-generated rather than hand-authored, low value.
        **FINDING:** not tested this pass.

      49. `BACKUP`/`RESTORE` and `CREATE DATABASE` forbidden option
        combinations, decidable purely from the statement's own option
        list — DBA-maintenance-script scope, not typical application SQL.
        **Shipped for one concrete combo:** `BACKUP DATABASE ... WITH
        DIFFERENTIAL, COPY_ONLY` always fails — a copy-only full backup
        never registers as a differential base, so any later differential
        can never find "a current database backup" to diff against (`Msg
        3035`). Shipped as `BackupOptionConflictRuleId`. `RESTORE` and
        `CREATE DATABASE` combos not tested, remain open.

      50. PolyBase/Hadoop external-table column-type and virtual-column
        restrictions — mainstream on-prem feature but low adoption.
        **FINDING:** not tested this pass. PolyBase is now installed and
        enabled (`IsPolyBaseInstalled` = 1, `polybase enabled` on) on both
        local instances, `CREATE EXTERNAL DATA SOURCE` confirmed working —
        the infra gap is closed, see `docs/local-dev.md`.

      51. A typed XML variable resolving to a different/missing schema
        collection than its type metadata records — rare in normal
        authoring. **FINDING:** not tested this pass.

      52. Remaining leg of the `DropProtectedObjectRuleId` family (`DROP
        SCHEMA` non-empty, `DROP ROLE` fixed role — shipped): `DROP EXTERNAL
        DATA SOURCE`/`DROP EXTERNAL FILE FORMAT` blocked by a dependent
        external table/stream. **FINDING:** not tested this pass — PolyBase
        is now available locally (see item 50), infra gap closed.

      53. Ledger tables restrict which `ALTER COLUMN` shapes are legal
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
        evidence of a restriction — left open rather than dropped, since
        this is inconclusive, not debunked.

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
