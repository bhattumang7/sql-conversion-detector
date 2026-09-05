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

- [ ] **Computed-column result type is never inferred, so `UnsupportedColumnType`
      can never fire on one.** Surfaced chasing a `TEXTPTR`-in-a-computed-column
      oracle test for the determinism checker: a full-text index on a
      `TEXTPTR`-rooted computed column actually fails with Msg 7670 ("not a
      character-based, XML, image or varbinary(max) type column") because
      `TEXTPTR` always returns a fixed `VARBINARY(16)` - but `CatalogColumn.Type`
      is always `null` for a computed column (only declared-type columns get a
      resolved `SqlType`), so `FullTextIndexDdlScanner`'s `UnsupportedColumnType`
      branch (and any other scanner keyed off a column's own type) can never
      see this. Needs computed-column expression type inference wired through
      `CatalogBuilder` (likely via the existing `ExpressionTypeInferencer`) before
      any type-shaped rule can reach a computed column at all - a modeling gap,
      not a one-off fix.
- [ ] **Systematic sweep of the full internal builtin-determinism metadata against
      Microsoft's documented list and the shipped checker.** This session
      extracted the engine's own real per-function determinism metadata (~646
      deduplicated named entries, oracle-methodology recorded in
      `detection-reference.md`'s "Built-in function determinism" section) and
      only spot-checked a double-digit sample against
      `ComputedColumnDeterminismChecker` - enough to find and fix the
      `CURRENT_TIMESTAMP`/`ParameterlessCall` gap and corroborate the
      `MIN_ACTIVE_ROWVERSION` docs error, not enough to call the list
      exhaustively diffed. A full pass (every deduplicated entry, name
      resolvable to a real user-callable T-SQL construct, cross-checked against
      the docs page and the checker's current denylist, each surviving delta
      oracle-confirmed via `PERSISTED` computed column rejection) could surface
      more gaps the same way. The raw local extraction from the prior session
      can be regenerated if needed - not repo-tracked.

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
