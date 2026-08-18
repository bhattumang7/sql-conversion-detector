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

- [ ] **Non-aligned index on a partitioned table.** Deferred for want of data,
      not design: the local test database has zero partitioned tables, so the
      rule would ship unexercised. Needs a partitioned-table corpus, plus new
      catalog surface (`sys.partition_schemes`, `sys.indexes.data_space_id`,
      `sys.partition_functions`).

### Tooling

- [ ] **Machine-readable rule catalog.** *In progress.* `RuleCatalog` becomes
      the single source for each rule's id, severity, rationale, fix-guidance
      and example; `docs/rules.html` and SARIF's `rules` block both generate
      from it.

### Engineering debt

Do these when the touched code is being worked on anyway.

- [ ] **Separate rule decisions from ScriptDom traversal.** What's left is
      rules decided inline in visitors. Shared traversal is done
      (`ScopedSqlVisitorBase`, `PredicateTreeWalker`); a generic
      `CollectorVisitor<T>` was designed and rejected — the `Flatten*`
      signatures diverge too much for it to pay.
- [ ] **Hand-threaded `(sourcePath, StartLine, StartColumn)` triples, ~84 call
      sites.** Blocked on a decision, not on effort: a `SourceSpan(node)`
      helper shortens argument lists only, while having the records carry a
      `SourceSpan` changes the public JSON shape of every finding type at once.
      Pick one before writing code.
- [ ] **Per-instance confidence.** Fixed per rule type today; varying it per
      finding instance matters for a handful of rules.

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
