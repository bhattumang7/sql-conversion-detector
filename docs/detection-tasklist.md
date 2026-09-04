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
