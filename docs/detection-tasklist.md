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

      20. Broaden the float-non-determinism family (aggregate argument,
        already written up) to float-typed arithmetic operands generally
        and float constants in precision-sensitive expressions — likely one
        rule, not three. **FINDING:** not tested this pass.

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
