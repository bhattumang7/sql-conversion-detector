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

### Docs

- [ ] **Per-rule pages: fill the remaining ~161/234 rules.** Shipped:
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
      (`RulesDocGeneratorTests`) byte-compares against `docs/`. 73/234 rules
      have a `RuleDocContent` entry today (tier1, verdict/scan-forced+range-
      seek, write-loss, tvf-fence, scalar-udf, a chunk of catalog/predicates/
      call-graph, query-anti-pattern, trigger-correctness, forced-serial,
      cross-module, correctness/dml/join/query singles) — a rule with no
      entry still renders (short rationale only, humanized title, no
      fabricated fix/example section), just thinner. Remaining backlog:
      index-design (~20), formatting/naming/dead-code/duplication/deprecated-
      syntax/code-metrics (~50, lower value - mostly self-evident from their
      name), statement-shape, control-flow-risk, security, database-
      configuration, hint, session-date-setting, cartesian-join, undersized-
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

- [ ] **Phase 0 — precision hotfixes.** Straight bugs, no architecture; each
      needs its fires/clean fixture pair per the working agreements.
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
      3. `Collation.cs:45-48`: `EndsWith("_BIN2")` misses
         `*_BIN2_UTF8` collations → `SargabilityClassifier` advises deleting
         an `UPPER()` wrap that changes results. Match `_BIN`/`_BIN2` as a
         segment, not a suffix.
      4. Tuple-keyed sets compare ordinal-case-sensitively (invisible to a
         `StringComparer` grep): `CompositeIndexLeadingColumnScanner.cs:44/69`,
         `IndexHintScanner.cs:84/150`,
         `PartialCompositeForeignKeyJoinScanner.cs:130` (+ its
         `NormalizedPair` ordering at :304),
         `NotInNullableSubqueryScanner.cs:134` (mixed Ordinal/IgnoreCase in
         one expression), `ScanReportBuilder.cs:1420` outputSummaryIndex.
         These are suppression sets, so a case miss = false positive. Promote
         `TypedPredicateExtractor.TableColumnKeyComparer` (:236) to shared.
      5. `EXEC(...) AT linked_server` analyzed against the local catalog —
         `ExecuteSpecification.LinkedServer` is read nowhere. Decline with a
         machine-readable reason + count in `DynamicSqlSummary`, mirroring
         `FromScopeResolver.cs:248`'s four-part-name guard.
      6. `DuplicationScanner.cs:307` reports `Col = Col` as a tautology at
         High confidence — it's the idiomatic NULL filter on a nullable
         column and the advice changes results. Decided (Umang, 2026-08-19):
         catalog-gated — wire the catalog into `DuplicationScanner` and fire
         only when the column is provably NOT NULL; stay quiet otherwise.
      7. `DynamicSqlTransfer.cs:1055,:1095`: two discarded `TryEmitFromValue`
         returns drop a dynamic-SQL call site from every `DynamicSqlSummary`
         bucket including `TotalCallSites` — silently counted as clean.
      8. Seven sorts still missing a total tiebreak under unordered PLINQ
         (`ScanReportBuilder.cs:1189` tier1 omits `Kind`; `:1194` dynamic-sql
         omits Column/Outcome; `:529`, `:565`, `:1028`, `:1053`, `:1093`).
         Same class as commit 849f89f's five.
      Deferred to their own decision, not forgotten: guard-correlated branch
      cross-product in `SqlTextValue.Concat`/`ForkAssemblies` (needs guard
      bookkeeping through concat — design first), `sp_prepare`/`sp_execute`
      recognition, `SelectIntoColumnResolver` ambiguous-alias poisoning +
      CTE shadowing (align with `FromScopeResolver.cs:129`'s poison rule).
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
