# SilentScan — Initial Plan

Goal: static analyzer for index-killing implicit conversions in T-SQL with
multi-layer view lineage, plus a corpus study with engine-verified findings.
Success = a publishable writeup with two defensible numbers:
(1) prevalence — "X% of scanned projects contain ≥1 confirmed index-killing
conversion, Y% of those inherited through ≥1 view layer";
(2) cost — "one such predicate costs N× logical reads at 1M rows."

---

## Phase 0 — Spike (1–2 evenings)
Prove the two risky bits before building anything real.
- [x] Console app: ScriptDOM (TSql160Parser) parses one file containing a
      table, two stacked views, and a proc; dump the AST; extract the WHERE
      predicate's column reference.
- [x] Docker SQL Server 2022 up; deploy that same file (DDL only, tables stay
      empty); query sys.columns for the views; submit a self-authored probe
      SELECT under SET SHOWPLAN_XML ON (compile-only — nothing executes) and
      locate CONVERT_IMPLICIT on the column side of the estimated plan.
- Exit: we can see both the static AST and the engine's ground truth for the
  same artifact. Everything else is plumbing.

## Phase 1 — Catalog + literal rules (week 1)
- [x] Pass 1 catalog: CREATE/ALTER TABLE, CREATE INDEX, PK/UQ constraints,
      collations, temp tables, table variables.
- [x] Precedence matrix + literal typing rules encoded, with unit tests per
      type pair we intend to report (start narrow: varchar/nvarchar,
      char/nchar, int/varchar, datetime/varchar).
- [x] Tier-1 syntactic rules (no types needed): function-on-column,
      cast-on-column, leading-wildcard LIKE, column arithmetic.
- Exit: `silentscan scan folder/` emits Tier-1 findings as JSON on fixtures.

## Phase 2 — Lineage engine (weeks 2–3, the hard part)
- [x] View dependency graph, topological resolution, cycle → UNKNOWN.
- [x] Column provenance: BaseColumn / Expression / Cast / Unknown.
- [x] SELECT * expansion, aliases, UNION type unification (record all branch
      types), inline TVFs, nested subqueries in FROM.
- [x] Verify oracle: deploy each fixture to Docker, diff inferred view column
      types vs sys.columns — CI job, any mismatch fails the build.
- Exit: lineage inference is oracle-clean on all fixtures, including a
  5-deep view chain and a mixed-collation UNION.

## Phase 3 — Predicate analysis + verdicts (week 4)
- [x] Extract comparisons from WHERE/ON/HAVING in procs/views/functions.
- [x] Resolve column side through lineage; type the other side (literal,
      @param/@variable declarations, other column).
- [x] Verdict engine incl. collation rules (SQL_* scan vs Windows RANGE_SEEK)
      and column-side-only direction logic; depth + origin attribution
      ("mismatch introduced by CAST in vw_X line 12").
- [x] Dynamic SQL bucketing (unanalyzable counter).
- Exit: full pipeline on a synthetic mini-project reproduces every finding we
  planted, zero false fires on the clean twin fixtures.

## Phase 4 — Pilot on 5 real repos (week 5)
- [x] Corpus manifest; pick 5 open-source projects with real DDL+procs in-repo.
- [x] Scan → hand-verify every finding → oracle-confirm via Docker deploy +
      plan XML probe.
- [x] Measure precision; fix rules until >95% on this set. Record UNKNOWN and
      unanalyzable rates honestly.
- Exit/gate: if precision can't reach target, stop and rethink before scaling.

## Phase 5 — Benchmark harness (week 6, parallelizable)
- [x] Synthetic tables per reported type pair; 10K/1M/10M rows; matched vs
      mismatched predicates; both collations; both CE settings; MAXDOP 1;
      compat 160; median-of-5 warm runs; CSV out.
- Exit: the cost table that the writeup charts.

## Phase 6 — Full corpus + writeup (weeks 7–8)
- [x] Corpus stays at the 5-repo pilot set for this study (decided; not
      broadening for now).
- [x] Run, oracle-confirm the headline subset, aggregate stats incl. depth
      distribution (the unique number: % of findings inherited through views).
      See docs/study.md - 76/79 DNN findings oracle-confirmed, 0 refuted, all
      confirmed findings at depth 0 in this pilot sample.
- [x] Writeup: name the phenomenon, lead with the two numbers, methodology
      section that survives hostile HN reading (direction rule, collation
      handling, CE policy, UNKNOWN rates all stated up front), repro repo.
      See docs/study.md + docs/bench-results.csv.
- [ ] Ship order: blog post + Show HN + the tool (pip-adjacent UX: single
      dotnet tool install, one command, pretty output). Not yet published -
      needs Umang's explicit go-ahead before any external posting.

## Phase 7 — Distribution afterlife (optional)
- GitHub Action (SARIF), then a thin SonarQube plugin over the same engine if
  demand shows up. Not before the study ships.

---

## Dialect strategy (decided)
T-SQL only, ScriptDOM only. Dialect sniffing filters non-SQL-Server files out
of the corpus instead of us pretending to support them. Other dialects are a
possible v2 via separate front-ends emitting the same findings schema — the
lineage/verdict layers are dialect-agnostic by design, the parser is not.

## CE / settings strategy (decided)
Static verdicts are CE-independent (they describe predicate shape). Engine
confirmation keys on CONVERT_IMPLICIT-on-column in plan XML, never on whether
a tiny test table happened to seek or scan. Benchmarks pin compat 160 and run
both CE modes and both collation families so the numbers can't be dismissed
with "you tested the wrong estimator/collation."

## First session with Claude Code
1. `git init`, solution + project skeleton per CLAUDE.md layout.
2. Phase 0 spike, both halves.
3. Commit the spike + the two fixture files it used — they become the seed of
   the test suite.
