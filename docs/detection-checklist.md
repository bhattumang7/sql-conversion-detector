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

### Docs

- [ ] **Per-rule pages: fill the remaining ~159/234 rules.** Shipped:
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
      (`RulesDocGeneratorTests`) byte-compares against `docs/`. 75/234 rules
      have a `RuleDocContent` entry today (tier1, verdict/scan-forced+range-
      seek, write-loss, tvf-fence, scalar-udf, a chunk of catalog/predicates/
      call-graph, query-anti-pattern, trigger-correctness, forced-serial,
      cross-module, correctness/dml/join/query singles, cartesian-join) — a
      rule with no entry still renders (short rationale only, humanized
      title, no fabricated fix/example section), just thinner. Remaining
      backlog: index-design (~20), formatting/naming/dead-code/duplication/
      deprecated-syntax/code-metrics (~50, lower value - mostly self-evident
      from their name), statement-shape, control-flow-risk, security,
      database-configuration, hint, session-date-setting, undersized-
      declaration, window-frame, view-ordering, module-compile-flag,
      dynamic-sql, lineage, and the rest of catalog (temporal-table-history-
      index-gap, cascading-fk, multi-referenced-cte, nested-view-depth,
      post-expansion-join-width, select-star-view/stale variant, try-cast-
      computed-column, non-persisted-computed-column, security-predicate-
      index, aggregate-division-columnstore). Also open: linking the rule
      page from the readable/console report per finding group; `helpUri` on
      the JSON findings schema. Do family-by-family, each its own commit (the
      per-rule-file-in-its-own-class pattern parallelizes well across
      subagents - each batch just needs the exact `SarifRuleCatalog` constant
      + current Rationale/FixGuidance text per rule, handed out per family).

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
      `TypedPredicateSummary`/`ParseHealth`, so "couldn't look" is
      indistinguishable from "clean" exactly where the contract forbids it;
      `scan-db` has no stderr warning or exit-code effect for parse failures
      (`scan-corpus-live` does). Collapse to findings + summaries consumed
      uniformly; SARIF gets the honesty channels via `invocations`/
      `notifications`. Decided (Umang, 2026-08-19): always warn on stderr +
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
