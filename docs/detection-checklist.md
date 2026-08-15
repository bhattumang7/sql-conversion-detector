# SilentScan detection checklist

The complete, gated candidate list of SQL Server query-level performance
problems this tool could detect. This is the working backlog — work items one
by one, check them off, and prune sections when they stop being useful.

Competitor tools are referenced generically throughout ("a commercial
schema-bound analyzer," "one small T-SQL type-checker") rather than by name —
deliberate, so nothing about a specific vendor lands in a public commit. The
real identities are recorded locally in `vendor/tool-references.md`
(gitignored, never committed) if a name is ever needed again.

Context that shapes the whole list: no living static tool does type-aware,
direction-aware, lineage-aware analysis; the tools that ever bound types flag
mismatches symmetrically and are dead or niche, while the purely syntactic
patterns are each covered by several existing linters. **Rule of admission: a
new detection should require the engine-authoritative catalog, the lineage
pass, or the plan-XML oracle to be possible at all. Precision beats recall;
every rule ships with a near-miss guard and, where verdict-bearing, an oracle
test.**

Base rates below are module counts from an aggregate pattern inventory of the
local production copy (crude LIKE-heuristic counts over ~5,000 modules —
signal, not findings; no schema details recorded here).

---

## Already shipped (context, not work)

- [x] Column-side implicit conversion in predicates — precedence direction,
      collation family (`ScanForced` vs `RangeSeek`), literal typing,
      view/iTVF lineage depth + origin, dynamic-SQL constant tracing,
      oracle confirmation via `CONVERT_IMPLICIT`-on-column.
- [x] Tier-1 syntactic non-sargability stream (`SargabilityFindingKind`):
      function-wrapped column, CAST/CONVERT on column, column arithmetic,
      leading-wildcard LIKE, non-literal LIKE pattern.
- [x] MSTVF-as-fence stream (`TvfFenceFindingKind`): direct FROM/JOIN,
      correlated `CROSS/OUTER APPLY` (ranked first — no engine version
      mitigation rescues it), fence inherited through a view/iTVF layer
      (`Lineage.TvfFenceMap`, including the inline-TVF-function-call-syntax
      case), standalone reference, `INSERT ... EXEC`. Dynamic-SQL folding and
      verify-corpus oracle confirmation both wired in
      (`TvfFenceProbeBuilder`/`TvfFenceVerifier`; marker:
      `PhysicalOp="Table-valued function"` / `StatementType="INSERT EXEC"`,
      oracle-verified directly). iTVFs never fire — oracle-verified against
      `sys.objects.type='IF'` on the local test DB.
- [x] Scalar UDF stream (`ScalarUdfFindingKind`): predicate-context invocation
      (WHERE/JOIN ON/HAVING/MERGE ON, ranked first), reached through
      view/iTVF expansion (`Lineage.ScalarUdfMap`, same depth/origin shape as
      the TVF-fence map), schema-level dependency (computed column/DEFAULT/
      CHECK constraint, catalog-only via `SchemaDependencyScanner` — no
      query-site AST needed), projection-context invocation (SELECT list/
      ORDER BY/GROUP BY/SET/variable assignment, ranked last). Every call
      carries the finding's own inlineability read (SQL 2019+ FROID: engine
      `is_inlineable` preferred, `ScalarUdfInlineabilityScanner`'s static
      blocker scan only ever explains, never asserts Inlineable on its own),
      non-schemabound constant-argument-folding defeat, and CLR data-access
      status. Dynamic-SQL folding and verify-corpus oracle confirmation both
      wired in (`ScalarUdfProbeBuilder`/`ScalarUdfVerifier`; two-probe design
      — pinned probe with `OPTION (USE HINT('DISABLE_TSQL_SCALAR_UDF_INLINING'))`
      confirms the function reference via a `<UserDefinedFunction
      FunctionName="...">` plan element, natural probe cross-checks the
      finding's own Inlineability read via `ContainsInlineScalarTsqlUdfs="1"`
      on the `StmtSimple`). Oracle-discovered and load-bearing: the hint does
      NOT propagate into a scalar UDF called from inside a view's own
      definition (a call that inlines away under the hint at the top level
      still inlines away identically with it, even though the same view
      referenced elsewhere shows the `UserDefinedFunction` element for a
      genuinely non-inlineable function) — so every probe targets the finding's
      underlying function directly, never through the referencing view,
      mirroring `TvfFenceProbeBuilder`'s identical choice. SchemaDependency
      findings are never probed — the constraint/computed-column definition
      text they cite is already engine truth. All findings share the same
      "never guess" catalog gate: a 2-part call that doesn't resolve in the
      scalar-UDF registry (built-in or truly unknown) never fires.

---

## Corrections to shipped work (clear before starting a new stream)

One confirmed false positive, one decision to re-examine, two omissions. No
new detections here. The false positive outranks every Tier 1 item: a shipped
rule that fires on a predicate the engine seeks is exactly the failure the
precision bar exists to prevent, and it is in the stream Tier 1 §4 builds on.

- [ ] **`JSON_VALUE(col, '$.path')` false-positives the shipped
      function-wrapped-column rule** (`silentscan/tier1/function-wrapped-column`,
      `Predicates/NonSargablePredicateScanner.cs`). Since 2016 the engine can
      match a `JSON_VALUE` expression to an indexed computed column with an
      identical definition and seek on it, so the blanket "function call wraps
      the column → non-sargable" test is wrong there. Fix: before firing on a
      `JSON_VALUE`/`JSON_QUERY`-wrapped column, look up a computed column whose
      definition matches the same expression and suppress the finding when a
      supporting index exists on it. Ships with a fire fixture (no matching
      computed column), a near-miss that must NOT fire (matching indexed
      computed column), and an oracle probe confirming the seek — the
      near-miss is the whole point of the fix, so it is not optional.
      Previously recorded only as a guard note inside Tier 1 §4, which
      understated it: this is a live defect in shipped output, not a
      constraint on future work.
- [ ] **Re-examine whole-plan `GetRangeThroughConvert` attribution in the
      plan-cache path.** `src/SilentScan.Live/Catalog/LivePlanCacheReader.cs:212`
      sets `hasRangeSeek` from `planXml.Contains("GetRangeThroughConvert")`
      and applies it to every conversion the detector finds in that plan.
      **This is deliberate, not an oversight** — the code comment states the
      marker is read plan-wide to match how `TypeMatrixGenerator`'s oracle
      probe reads the same signal, and `WorkloadVerdict`'s own doc comment
      discloses it. The reason to revisit: in the matrix generator each probe
      is a single authored predicate, so plan-wide and per-node are the same
      thing there, whereas a cached plan from real application SQL can carry
      several conversions at once — and the marker actually sits inside one
      specific seek predicate's `ScalarString`. Where those differ, a plan
      with one range-seek conversion marks its genuinely `ScanForced`
      siblings `RangeSeek` too. Decide with evidence rather than by argument:
      find (or author, in the standing Docker instance) a plan carrying a
      range-seek conversion and a scan-forcing conversion together, and see
      whether the misattribution is real. If it is, resolve the marker
      against the conversion node `ConvertImplicitDetector` already located.
      If multi-conversion plans can't actually arise here, record that and
      close the item — the current behavior is then correct as written.
- [ ] **Two shipped streams are missing from "Already shipped" above** — the
      write-loss stream (`silentscan/write-loss/*`: unicode-to-non-unicode,
      approximate-to-exact truncation, numeric scale narrowing, temporal
      precision loss) and collation conflict
      (`silentscan/verdict/collation-conflict`, Msg 468, two real string
      columns with differing resolved collations and no explicit `COLLATE`).
      Both exist in code with rule IDs in `SarifRuleCatalog`. A backlog that
      under-reports what shipped is the same rot the narrative plan file died
      of; record them with the same detail as the other four entries.
- [ ] **Fix the checklist's own numbering before it misleads anyone.** Section
      numbers are insertion-order accidents, not stable IDs — `### 13` doesn't
      exist anywhere in the file (12 jumps straight to 14), and there's nothing
      here equivalent to the stable identity shipped rules get
      (`SargabilityFindingKind`, `TvfFenceFindingKind`, `ScalarUdfFindingKind`,
      each backed by a real SARIF rule ID). "Do item 13" or "priority is items
      3–10" currently doesn't resolve to anything reliable. Two open questions
      to settle when this is picked up, not decided here: (a) renumber
      sequentially with no gaps, or drop numbers for slug-style anchors that
      survive future insertions/deletions the way the numbers can't; (b) fold
      the "Candidates from the incumbent rule catalogs" and "Candidates from
      the wider product landscape" sections into the actual Tier 1/2/3
      structure — they currently sit outside it, so their priority relative to
      the numbered Tier 1/2 items is undefined. Most items in those two
      sections read as Tier 1 or Tier 2 by the same criteria (precision,
      catalog requirement, base rate) used everywhere else; a few of the
      "afternoon-sized" ones belong folded into the existing small-adds section
      instead of standing alone.
- [ ] **Record the runtime incumbent in `detection-reference.md`** as
      Appendix 7 §7.7, in the same anonymized style as the other entries: a
      multi-platform commercial response-time analyzer, read at source from a
      decompiled tree held locally and deliberately not in the repo, so the
      appendix has to stand on its own without it.
      Load-bearing detail: its entire SQL Server plan-advice surface is two
      XPath queries — `//MissingIndexes/MissingIndexGroup` and
      `//UnmatchedIndexes/Parameterization/Object`, matching the whole of its
      advice-type enum (missing index, unmatched index) — and it *does*
      detect implicit conversions, as a substring test for `CONVERT_IMPLICIT`
      over a plan step's predicate text, yielding one boolean per step with
      no notion of which side converted. That makes it a real shipping
      instance of the failure mode the reference currently poses only
      hypothetically ("even a plan-reading tool that greps for the marker
      without checking which side it wraps reproduces both errors"), so cite
      it there too. Its remaining analyses are all runtime aggregates (wait
      events, blocking, plan stability, execution counts) or specific to
      another DBMS entirely — so it opens no detection gap on our side, and
      §J plus the Tier 3 index-advisor skip already cover every SQL Server
      capability it has.

---

## Tier 1 — build next (high precision, needs our machinery, high base rate)

### 3. Join-key and cross-object type/collation mismatch
Direct reuse of the precedence matrix on new predicate sites. Production copy:
148 column names occur across tables with differing type/length/collation.

- [ ] Type mismatch across JOIN `ON` columns — same column-side/precedence
      analysis as WHERE, same collation verdicts, same oracle probe.
- [ ] Proc/function **parameter type vs compared column type** — argument
      conversion at the call boundary; includes a variable passed to a proc
      whose param type mismatches downstream columns.
- [ ] **Column collation ≠ database collation** — schema-side conversion
      seed; pure catalog diff.
- [ ] Cross-table same-name type drift report (FK pairs and join-candidate
      pairs with differing types) — catalog-only, feeds the study's
      "conversion seeds" narrative.
- [ ] `sql_variant` in comparisons — highest precedence, so the real column
      always converts. Trivial matrix extension; rare (4 columns locally)
      but zero-cost to add.
- Oracle: identical `CONVERT_IMPLICIT`-on-column probe as the existing stream.

### 4. Type-aware upgrade of the sargability stream
Highest base rate of anything measured: ~1,100 modules with ISNULL/COALESCE
in WHERE clauses; 96 with RTRIM-family wrappers; 54 with leading wildcards.

- [ ] `ISNULL(col, x) = y` / `COALESCE(col, x) = y` in predicates — upgrade
      from syntactic flag to verdict: is the column nullable (catalog)? does
      an index exist? does COALESCE's result-type inference (highest
      precedence operand) flip a conversion onto the column?
- [ ] Date-form non-sargables as named rules: `YEAR(col)=`, `DATEPART` on
      column, `DATEADD/DATEDIFF` on column, `CONVERT(varchar, col, n)`
      comparisons, BETWEEN with end-of-period boundary.
- [ ] `CHARINDEX(x, col)` / `LEFT(col, n) =` — rewritable-to-sargable forms.
- [ ] UPPER/LOWER on column **checked against actual collation** — fires only
      when the column's collation is case-sensitive (existing linters assume
      case-insensitivity blindly). Collation-aware = our edge.
- [ ] Index-existence weighting for all sargability findings: an unsargable
      predicate on an unindexed column is noise; on an indexed column it's a
      lost seek (we already rank expression findings by underlying index —
      extend to the whole stream).
- **Mandatory precision guard for every rule in this section:** a function
  wrapping a column does not imply a lost seek when an indexed computed column
  matches the same expression. Tracked as a live defect against the
  already-shipped function-wrapped-column rule in "Corrections to shipped work"
  above, with the fix and its fixtures; land that first, then hold each new
  rule here to the same guard rather than re-deriving it.
- Oracle: seek-vs-scan probes; for the collation rule, CS vs CI fixtures.

### 5. Oversized and MAX-typed parameters
Under-represented in existing rule sets, adjacent to the existing conversion
code, high precision.

- [ ] `varchar(max)`/`nvarchar(max)` parameter or variable compared to a
      `(n)`-typed column — blocks predicate pushdown even when the base type
      matches; no seek.
- [ ] Parameter declared longer than the compared column (`varchar(200)` param
      vs `varchar(50)` column) — memory-grant inflation; lower severity.
- [ ] MAX-typed columns used as predicate/join targets (can't be an index
      key) — catalog-only report.

---

## Tier 2 — strong candidates (precise rules exist, new machinery needed)

### 6. Catch-all / kitchen-sink predicates
425 modules in the production copy (`... OR @param IS NULL`).

- [ ] `(col = @p OR @p IS NULL)` and COALESCE/ISNULL-disabled optional
      filters — one plan must serve all parameter combinations.
- [ ] **Precision guard (mandatory):** `OPTION(RECOMPILE)` on the statement
      largely neutralizes it — detect and either suppress or downgrade.
      70 modules locally already use OPTION(RECOMPILE).
- [ ] Sibling: parameter overwritten before use in a predicate
      (sniffing-defeat — straight-line dataflow we already have from
      dynamic-SQL tracing).

### 7. Local-variable predicates
- [ ] `WHERE col = @v` where `@v` is DECLAREd in the batch (not a parameter) —
      density-vector estimate instead of sniffed value. Distinguishable from
      parameters purely in the AST; same OPTION(RECOMPILE) guard as #6.

### 8. NOT IN over a nullable subquery column
346 modules locally use `NOT IN (SELECT ...)`.

- [ ] Fires **only** when the catalog says the subquery column is nullable —
      correctness trap plus expensive null-aware anti-semi-join. The
      nullability gate is what makes this precise where linters spray.

### 9. UPDATE ... FROM without source uniqueness
- [ ] Target joined to a source whose join columns carry no PK/unique
      constraint — nondeterministic multi-match update (each target row takes
      an arbitrary source row). Catalog-gated; correctness-first finding with
      a real perf angle (MERGE raises where UPDATE silently picks).

### 10. Forced-serial construct inventory
- [ ] Table-variable **modification** (INSERT/UPDATE/DELETE @t) — whole plan
      serial; read-only use does not fire (direction-style distinction).
      821 modules locally use table variables.
- [ ] Dynamic/keyset cursors; cursor without `LOCAL FAST_FORWARD` as the
      crisp subrule (197 modules with cursors locally).
- [ ] The finite serial-forcing intrinsics list (IDENT_CURRENT, ERROR_NUMBER,
      @@TRANCOUNT, OBJECT_ID, ...) in queries — encodable, documented.
- [ ] Serial-zone constructs as informational: TOP row goals, recursive CTEs,
      MSTVF refs (already shipped — MSTVF-as-fence stream), global scalar aggregates.
- Note: several of these fold naturally into the scalar UDF stream (#1) or
  the already-shipped MSTVF-as-fence stream rather than standing alone —
  decide at design time.

### 11. Lineage-metric findings (cheap adds on existing passes)
- [ ] Nested-view depth report — we already compute topo order; emit depth ≥ N
      as a finding with the chain (57 views reference other views locally).
- [ ] Multi-referenced CTE — inline macro re-executed per reference; count
      references in the AST. Rarely covered anywhere; high precision.
- [ ] Untrusted (WITH NOCHECK) FK/CHECK constraints — optimizer forfeits join
      elimination; pure catalog flag (`is_not_trusted`).
- [ ] Cascading FK actions (ON DELETE/UPDATE CASCADE) — hidden serial
      multi-table work per DML; catalog-only, informational.

### 12. Dynamic SQL quality (extends the existing dynamic-SQL pass)
123 modules use `EXEC(...)`, 51 use sp_executesql locally.

- [ ] Concatenated **value** (vs identifier) in proven-constant dynamic SQL —
      unparameterized: plan-cache pollution + per-literal compiles. We already
      prove constancy; classifying concatenation operands as value/identifier
      is incremental.
- [ ] `EXEC(string)` where sp_executesql with params was possible (only when
      the taint analysis shows a parameterizable value) — report, don't guess.
- [ ] **Temp-table shape mismatch across a proc-call boundary** — one small
      T-SQL type-checker in the wider tool landscape checks a version of this;
      ours would be catalog+plan-backed, not a type-inference heuristic. T-SQL
      scoping means a `#temp` table created by a calling proc stays visible to
      a proc it calls during that call, and `INSERT INTO #temp EXEC OtherProc`
      assumes the executed proc's result-set shape matches `#temp`'s declared
      columns — silently wrong column count/order/type is either a runtime
      error or, worse, a silent per-column implicit conversion that our
      existing conversion stream would never see because it never runs on the
      `EXEC`'d proc's own SELECT list against the caller's temp-table DDL. This
      is squarely in-scope by CLAUDE.md's own boundary: temp tables created
      inside proc bodies are explicitly one of the module-body objects we
      already parser-model (the engine doesn't expose them). Ground truth for
      the executed proc's actual shape: `sys.dm_exec_describe_first_result_set`
      (compile-only, same guarantee the `scan-db` describe-only path already
      relies on) rather than re-deriving it from the proc's own SELECT list.
      Verdict: column-position type mismatch between the temp table's DDL and
      the executed proc's described result set. Oracle: compile-only probe of
      the `INSERT ... EXEC` batch under SHOWPLAN_XML, same discipline as the
      shipped streams.

### 14. Schema-scan UDF and computed-column findings (found on completeness audit)
Distinct trigger from #1's plan-based UDF findings: these fire from catalog
metadata alone, independent of whether the object ever shows up in a cached
plan, so they need no plan/oracle involvement to report (though should still
get an oracle fixture for the serial-plan consequence).

- [ ] CHECK constraint whose definition references a scalar/CLR function —
      forces serialized execution of every query and maintenance operation
      against the table. Pure catalog scan (`sys.check_constraints`
      definition text against `sys.objects` function list).
- [ ] Non-persisted computed column (`is_persisted = 0`), independent of
      whether it references a UDF — recomputed on every read; broader trigger
      than the UDF-specific rule in stream #1, catalog-only.
- [ ] Deprecated `*=`/`=*` outer-join operators — legacy syntax that can
      silently change join semantics and plan shape across engine versions;
      pure AST syntax check, near-zero FP risk, cheap to add.

### 15. Halloween Protection and self-referencing DML
- [ ] `INSERT`/`UPDATE`/`DELETE`/`MERGE` whose source query reads the same
      target table (hole-filling `INSERT ... WHERE NOT EXISTS`,
      `UPDATE ... FROM` self-join) — forces a blocking eager spool, distinct
      mechanism from the UDF-in-DML case already covered. Pure syntax: target
      table object also appears in the statement's read-side FROM/subquery.

### 16. Temporal table history-side index gap
- [ ] System-versioned temporal table (`sys.tables.temporal_type`) whose
      history table lacks the index set the current table has — `FOR
      SYSTEM_TIME AS OF/BETWEEN` queries rewrite to a UNION ALL between the
      two tables, so a sargable predicate on the current side does nothing
      for the history side, silently forcing a scan on half the union.
      Catalog-only: compare index definitions between `parent_id` and
      `history_table_id`.

### 17. Small precise adds (each an afternoon, not a stream)
- [ ] Proc authored `WITH RECOMPILE` — compiles every call, invisible to
      cache-based monitoring; pure catalog flag (`sys.sql_modules`).
- [ ] `RANGE` instead of `ROWS` in window-function frames — on-disk spool per
      partition; purely syntactic, near-zero FP risk.
- [ ] Trigger content scan — run trigger bodies through the existing pipeline
      so cursors/UDFs/MSTVFs inside triggers surface as hidden per-DML cost
      (the modules are already in `sys.sql_modules`; this is mostly wiring).
- [ ] `COMPUTE`/`COMPUTE BY` deprecated aggregate constructs — bypasses
      normal set-based aggregate optimization; syntax-only, rare but trivial.
- [ ] `WAITFOR DELAY`/`WAITFOR TIME` inside a routine or batch — holds a
      worker thread idle, contributing to worker exhaustion under load;
      syntax-only.
- [ ] Transaction hygiene pair: lengthy work (loops, RBAR, external calls)
      between an error and its `ROLLBACK` extends lock hold duration;
      `BEGIN TRANSACTION` with no reachable `ROLLBACK`/`COMMIT` on some path
      leaves locks held indefinitely. Both are control-flow/dataflow checks
      over the AST, no catalog needed.

### 18. Candidates from the incumbent rule catalogs
~638 rules across six analyzers, extracted in full to
`detection-reference.md` Appendix 7 (read that before adding anything here —
the complete lists are there, including the categories we don't play in).
Most of it is syntax-only and already covered or already skipped. These are the
items that need our catalog and that nobody does properly. Several sit under
the incumbents' *Design* or *Execution-issue* headings, not Performance, which
is why a performance-tagged-only reading missed them.

- [ ] **Pre-publication gate: measure the second type-binding incumbent's
      conversion rule against our direction fixtures.** The commercial
      schema-bound analyzer previously recorded as dead is in fact alive, and
      its cross-type-operator rule is genuinely connection-bound. Its docs
      read as symmetric, but that's an unverified negative (vendor site
      defeats fetching). Trial-install it and run the same three-case demo
      used against `SRP0016`; the study cannot claim "nothing is
      direction-aware" in public until this is measured.
- [ ] **Join predicate incomplete vs. the backing foreign key** — a join
      missing a backing FK entirely, or joining on fewer columns than a
      composite FK defines. The partial-composite case is a real correctness
      *and* plan defect (silent row multiplication) and is pure catalog work.
      Strongest single find of the catalog read; nobody resolves it properly.
- [ ] **Temp-table / table-variable collation vs. database and tempdb
      collation** — a conversion *seed* on every join between a temp object and
      a user table, and the classic cause of collation-conflict errors. We are
      the collation-aware tool and this is squarely ours; pairs with the
      already-queued "column collation ≠ database collation" in #3.
- [ ] **SET options that silently disable plan features** — QUOTED_IDENTIFIER
      OFF and NUMERIC_ROUNDABORT ON mean **indexed views and filtered indexes
      cannot be used** by that module. Universally filed as style hygiene; the
      actual consequence is plan-shape, and it is catalog-verifiable per module
      (`sys.sql_modules.uses_quoted_identifier`/`uses_ansi_nulls`), not a
      guess. Precise, cheap, and mis-framed everywhere else.
    - **ARITHABORT is a related but structurally different case — verified
      against `sys.all_columns` on `sys.sql_modules`: it is NOT one
      of the baked-in settings (only `uses_ansi_nulls` and
      `uses_quoted_identifier` are).** ARITHABORT is purely a connection/session
      setting, invisible to catalog inspection of the object itself — so this
      cannot be a catalog fact the way the QUOTED_IDENTIFIER/NUMERIC_ROUNDABORT
      finding is. The only static surface is an explicit `SET ARITHABORT OFF`
      statement in the T-SQL text (syntax-only, same shape as the already-shipped
      FORCEPLAN/deprecated-SET-option rules). Real-world story worth the
      afternoon: SSMS defaults the setting ON, most driver/app connections
      default it OFF, so the *same* proc gets different cached plans depending
      on which kind of connection first compiled it — a classic "fast in SSMS,
      slow from the app" symptom. To clear the precision bar this needs a
      guard: only fire when the module's dependency graph actually touches a
      table with a filtered index or an indexed view (catalog-derivable via the
      same dependency walk the schema-scan UDF stream already does) — an
      explicit `SET ARITHABORT OFF` in a module that never touches either is
      noise. Oracle: compile/connect with ARITHABORT ON vs OFF against a query
      touching a filtered index and diff the plan or the plan-cache entry.
- [ ] **Hint validity against the catalog** — every surveyed tool flags
      `INDEX`/`JOIN`/`TABLE`/`QUERY` hints as a *style* smell ("avoid hints").
      The catalog-requiring version nobody does: an `INDEX(...)` hint naming an
      index that **no longer exists**, or pinning an index the predicate cannot
      seek anyway, so the hint forces a scan of the wrong index.
- [ ] **Composite index leading-column violation** — predicate filters a
      non-leading key column while the leading column is unconstrained. One
      surveyed tool has this as a regex; against real index key ordering it
      becomes precise. Scope it as a *predicate* finding ("this query cannot
      seek this index"), never as an index recommendation, or it drifts into
      the index-advisor skip.
- [ ] **`ISNULL`/`COALESCE` arguments of differing datatypes** — folds into the
      T1-4 COALESCE result-type inference work rather than standing alone; the
      incumbent rule flags the mismatch but never computes the result type, so
      it cannot say whether a conversion lands on the column.
- [ ] **`TOP(100) PERCENT` ignored by the optimizer** and **`ORDER BY` in a
      view / inline TVF** — same family, both commonly written to "force"
      ordering that is not guaranteed. Syntactic, near-zero FP, afternoon-sized
      (belongs with #17 if picked up).
- [ ] **`IF` statements containing queries inside a procedure** — estimation
      and recompile consequences; nobody frames it as a performance finding.

### 19. Candidates from the wider product landscape
Found while independently answering "does anyone else detect this" beyond the
six repos — a check across distribution channels (marketplaces, NuGet, GitHub
topics) and curated indexes (curated tool directories, awesome-lists, the
official commercial SonarQube-ecosystem T-SQL rule set — now access-gated
behind a paid edition and its public docs taken down — plus academic
literature). Confirms the conversion-direction niche is still unclaimed;
nothing found does direction/collation/lineage-aware detection. Two items
worth pulling in:

- [ ] **Open scope question, not decided — CHECK-constraint-as-enum dead
      predicate.** One tool found — a small, PoC-labeled T-SQL
      type-checker doing a different bug class from ours (null-safety/join-key/
      temp-table-shape, not sargability) — treats a `CHECK (col IN (...))`-style
      constraint as defining an implicit enum and flags code that compares the
      column against a value outside it. Catalog-derivable for us the same way
      `SchemaDependencyScanner` already reads constraint definition text: parse
      `sys.check_constraints` definitions for an `IN`-list or OR-chain-of-equality
      shape, then flag a predicate elsewhere comparing that column against a
      literal proven outside the set — the predicate can never be true, so the
      query (or branch) is dead. **Unresolved:** this is fundamentally a
      correctness finding (always-false predicate), the same family as the
      incumbents' "comparison always evaluates to TRUE/FALSE" rule, which they
      file under Design, not Performance. Tier 2 item #8 (NOT IN over a nullable
      subquery column) was kept specifically because it has a real perf angle
      beyond the correctness bug (an expensive null-aware anti-semi-join); this
      candidate doesn't obviously have an equivalent extra angle. Needs a
      decision, not a default accept — record the reasoning here when it's
      made either way.
- [ ] **Follow-up gate, same shape as the pre-publication gate above: two new
      tools found need a closer look before being ruled out.** One
      (Rust, WASM-delivered, ~103 T-SQL rules, actually issues `SET PARSEONLY`
      and rollback-plan probes — the closest oracle discipline to ours found
      anywhere; actively maintained) and one distributed via
      NuGet (169 rules across security/correctness/performance/convention,
      actively published). Docs for both show no implicit-conversion hit on a
      grep, but that's from docs, not a source read — same caveat class as the
      pre-publication gate item. Lower priority than that one since neither
      claims schema-binding, but cheap to close: read their rule source before
      the study cites "nothing else exists" as settled.

Reporting ideas worth stealing (not detections):
- [ ] **Confidence tiers as a first-class output axis** — one surveyed tool
      gates findings Proven/Contextual/Advisory with a CI-safe default. Maps
      onto our `Verdict` + `Unknown` split and the "static-only findings go in
      an appendix" rule; would make the SARIF export a safer CI gate.
- [ ] **Source-context classification** (migration/deployment script vs
      hot-path module) used to filter before reporting — a one-off deployment
      script legitimately does things a proc must not.
- [ ] **Machine-readable rule catalog generated from the rule types**, carrying
      id/severity/rationale/examples/fix-guidance, feeding both docs and the
      SARIF `rules` block — keeps documentation and code from drifting apart.

**Security, compliance, and correctness-only rules are a live open question,
not a decided skip.** The incumbent catalogs carry a substantial security axis
(one tool devotes 61 rules to it — its second-largest category —
and the actively-developed DacFx pack ships a SQL-injection rule). Nothing has
been decided here; the full lists are in Appendix 7 §7.4 so the decision can be
made from the actual rules rather than from a category label. Note the one
genuine overlap already on the backlog: `EXEC(string)` where `sp_executesql`
was possible (T2-12) is admitted on plan-cache grounds, with the injection
surface as a side effect.

---

## Tier 3 — deliberate skips (decided; don't re-litigate without new evidence)

- **Parameter sniffing** — runtime data-distribution problem; static tools can
  only flag risk factors → hedged findings violate the precision bar.
- **SELECT \*, SET NOCOUNT, sp_ prefix, schema-prefix, ORDER BY ordinal,
  style/correctness linting** — crowded syntax-only territory several linters
  already cover; diluting into a generic linter destroys the tool's identity.
  (Missing schema prefix and unparameterized ad-hoc SQL are the only ones
  with real perf teeth, and both are plan-cache problems, not plan-shape
  problems.)
- **Missing/duplicate/unused indexes, heaps, fill factor, clustering-key
  width** — index-advisor space; catalog-only, no query analysis, different
  tool.
- **Runtime-only signals** — spills, memory grants, execution frequency,
  compile time, stale stats, plan-cache duplication, row-estimate mismatch:
  by definition not static. The oracle stays compile-only.
- **NOLOCK / READ UNCOMMITTED** — correctness smell wearing a performance
  costume; only 17 modules locally; linters cover it.
- **MERGE pitfalls** — real but correctness-focused and version-dependent;
  19 modules locally. Revisit only if a precise perf-framed subrule emerges
  (`WHEN MATCHED THEN DELETE`, missing HOLDLOCK).
- **DISTINCT-masking-bad-join, correlated-subquery-won't-unnest, row goals,
  UNION vs UNION ALL** — harm depends on optimizer decisions we'd be
  guessing; low-precision by nature. Inventory-grade at best.
- **Indexed view NOEXPAND matching** — edition-dependent and matching-logic
  FP risk; revisit if the corpus shows indexed views at all.
- **OR across different columns** — detection trivial, harm imprecise
  (index union often fine). Only viable as an index-aware variant; parked.
- **Partition elimination defeat** (non-literal/wrapped predicate on the
  partitioning column) — real and distinct from b-tree seek/scan, but needs
  partition function/scheme catalog modeling we don't have yet. Revisit if a
  corpus repo turns out to use partitioning.
- **Always Encrypted column-comparison restrictions** — high precision in
  principle (catalog exposes `encryption_type`), but needs a target using the
  feature to matter at all; the local production copy and pilot corpus don't.
  Revisit if that changes.
- **Batch Mode on Rowstore eligibility loss** — deterministic in principle,
  but Microsoft doesn't publish a fully canonical disqualifier list, so
  completeness (and therefore precision) is a real risk; parked until a
  trustworthy exhaustive list exists.
- **Window-function POC (Partition-Order-Covering) index shape** — real
  (missing index keyed PARTITION BY → ORDER BY → covering columns forces a
  Sort per partition), catalog + syntax detectable, but scoped as an
  index-advisor recommendation rather than a query-defect finding; revisit if
  the tool's scope ever grows to include index suggestions.
- **Query/order hint usage counters** (`sys.dm_exec_query_optimizer_info`
  join/order hint frequency) — inherently a runtime aggregate (counts since
  last restart), not a per-query static fact; the static form is already
  covered by the hard-coded-hints skip above.
- **SonarQube T-SQL rule coverage** — *resolved, no longer open.*
  The free-tier SonarQube T-SQL path is a community plugin, read at source: 16
  enabled T-SQL rules, all declarative ANTLR parse-tree shape matches, dormant
  since 2024, no implicit-conversion rule of any kind. Its non-sargability rule
  is `BETA` with no ground truth. The CI-gate niche is effectively unoccupied.
  Details in `detection-reference.md` → "Named incumbents".

---

## Cross-cutting requirements for every new stream

- Verdict-bearing rules ship an oracle fixture (compile-only SHOWPLAN_XML);
  syntactic-only rules ship fire + near-miss fixtures from real,
  internet-sourced bugs (no invented repros).
- Findings carry the same schema as conversions: verdict, indexed?, depth,
  origin (predicate site + introducing layer), machine-readable reasons.
- Every rule states its engine-version sensitivity (2017 interleaved
  execution, 2019 UDF inlining, 2022 CE behavior) rather than assuming one.
- Deterministic ordering; `Unknown` over guesses; unanalyzable counts
  reported honestly.
- The study angle for each shipped stream: prevalence at lineage depth ≥ 1
  ("inherited through views/TVFs") is the number nobody else can produce.
