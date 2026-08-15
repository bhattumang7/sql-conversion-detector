# SilentScan detection checklist

The complete, gated candidate list of SQL Server query-level performance
problems this tool could detect. This is the working backlog — work items one
by one, check them off, and prune sections when they stop being useful.

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

---

## Tier 1 — build next (high precision, needs our machinery, high base rate)

### 1. Scalar UDF stream
The #1 production killer after implicit conversions. Referenced by 73% of
procs in the production copy; 603 inline TVFs reference a scalar UDF, which
inlining spreads into every caller — a lineage-only detection no other tool has.

- [ ] Scalar UDF invoked in a predicate (WHERE/ON/HAVING): per-row execution,
      non-sargable, forces the whole plan serial (pre-inlining).
- [ ] Scalar UDF in SELECT list / per-row expression context (lower severity,
      still per-row + serial).
- [ ] Scalar UDF reached **through inline-TVF/view expansion** — lineage pass
      attributes the UDF cost to every consumer, with depth + origin like
      conversion findings. (603 iTVFs in the production copy carry one.)
- [ ] Scalar UDF in a **computed column, DEFAULT, or CHECK constraint** —
      forces every query touching the table serial, even queries not naming
      the column. Pure catalog detection (`sys.computed_columns`, constraint
      definitions). Production copy: 37 defaults + a handful of check
      constraints reference UDFs.
- [ ] **Inlineability classification** (SQL 2019+ scalar UDF inlining): body
      scan against the documented blocker list (GETDATE-class intrinsics,
      WHILE, TRY/CATCH, certain table-access patterns). A "will NOT inline"
      tag is what makes the finding damning on modern servers.
- [ ] Non-schemabound scalar UDF defeating constant-folding: engine won't
      treat it as deterministic → per-row execution even for constant
      arguments. Catalog flag check.
- [ ] CLR scalar UDF with data access (forces serial; catalog knows it's CLR).
- Guards: distinguish predicate vs projection context; report inlined-in-2019+
  cases at reduced severity only when the blocker scan proves inlineable.
- Oracle: plan shows UDF reference / `NonParallelPlanReason`; compile-only.

### 2. MSTVF-as-fence stream
41 MSTVFs in the production copy but 126 references (98 from procs, 20 from
other TVFs — nested fences). Call-site text is identical to harmless iTVFs;
only the catalog (`sys.objects.type` = 'TF' vs 'IF') can tell them apart.

- [ ] MSTVF in FROM/JOIN: optimization fence, no statistics, fixed 1/100-row
      estimate poisoning the surrounding plan.
- [ ] **Correlated `CROSS/OUTER APPLY dbo.fn(t.col)`** — executes per outer
      row; interleaved execution (2017+) explicitly does not rescue this.
      Rank first.
- [ ] MSTVF hidden under a view / another TVF — lineage depth + origin
      (the "permissions function wrapped in a view" case).
- [ ] Standalone `SELECT ... FROM dbo.fn(@x)` — informational tier only
      (fence exists, nothing around it to poison); precision guard against
      over-reporting.
- [ ] `INSERT ... EXEC` materialization (same family: forced full
      materialization to a worktable; cannot nest).
- Guards: iTVFs never fire; severity graded by usage context; note engine
  version mitigations (interleaved execution applies to uncorrelated only).
- Oracle: plan shows Table Valued Function operator with fixed estimate vs
  iTVF dissolving into base operators.

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
- **Mandatory precision guard, applies to the shipped function-wrapped-column
  rule too, not just new ones:** `JSON_VALUE(col, '$.path')` can match an
  indexed computed column with an identical definition and use that index —
  the engine has done this since 2016. A blanket "function call wraps the
  column → non-sargable" rule misfires here. Before firing on a
  `JSON_VALUE`/`JSON_QUERY`-wrapped column, check the catalog for a computed
  column whose definition matches the same expression; suppress if a
  supporting index exists on it. This is a real false-positive risk against
  our own precision rule, not just an enhancement — verify it's fixed before
  the next release that touches sargability rules.
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
      MSTVF refs (covered by #2), global scalar aggregates.
- Note: several of these fold naturally into streams #1/#2 rather than
  standing alone — decide at design time.

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
- **SonarQube T-SQL rule coverage** — last verification pass couldn't reach
  rules.sonarsource.com to confirm the performance-tagged rule list is
  current; treat prior Sonar coverage claims as unconfirmed, not complete,
  until re-checked from an environment that can reach the site.

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
