# CLAUDE.md — SilentScan

Static analyser + corpus study: find index-killing implicit conversions in T-SQL,
including ones inherited through layers of views/TVFs, and quantify their
prevalence and cost across public open-source codebases.

Two deliverables, one codebase: the **tool** (`silentscan scan`, JSON + SARIF
findings) and the **study** (`docs/study.md`, `docs/bench-results.csv`).
Fame goal, not commercial. **Precision beats recall everywhere** — one false
positive in the published study is worse than ten missed true positives.

Roadmap and phase status live in `plan.md`. Local setup in `docs/local-dev.md`.
This file is the standing contract; don't turn it back into a build plan.

## Hard scope (do not revisit without asking)

* SQL Server / T-SQL only. Parser is `Microsoft.SqlServer.TransactSql.ScriptDom`.
  No other dialects in v1, no ANTLR.
* EF Core / ORM analysis is out of v1. SQL text only.
* Corpus DML and procs are **never executed**, anywhere. The only execution is
  self-authored probes inside the disposable Docker SQL Server.
* .NET 10, C#. Ubuntu; Docker assumed available.
* Corpus stays at the pinned 5-repo pilot set unless we decide otherwise.

## Layout

`src/SilentScan.Core` holds the four passes — `Parsing/`, `Catalog/` (tables,
columns, types, collations, indexes), `Lineage/` (view/iTVF resolution, topo
order, column provenance), `Predicates/` + `Rules/` (extraction, precedence,
verdicts), `Reporting/` (ranked findings, JSON + SARIF), plus `Corpus/`.
Then `SilentScan.Cli`, `SilentScan.Verify` (Docker oracle), `SilentScan.Bench`,
and `tests/SilentScan.Tests`.

Each finding carries: `verdict`, whether the base column is **indexed**,
**depth** (0 = direct table predicate, N = view/TVF layers between predicate and
base column), and **origin** — both the predicate's file/line and the file/line
of the layer that introduced the mismatch. Rank `ScanForced` + indexed +
depth ≥ 1 first. `Verdict` is `SeekPreserved | RangeSeek | ScanForced | Unknown`;
syntactic non-sargability is its own finding stream (`SargabilityFindingKind`).

## The type rules (the heart of the tool — get these exactly right)

* **Only column-side conversion loses the seek.** T-SQL precedence converts the
  LOWER-precedence side. `varchar` column vs `nvarchar` value/param → the COLUMN
  converts → seek lost. `nvarchar` column vs `varchar` value → the VALUE converts
  → harmless (`SeekPreserved`). Direction errors are the #1 way this study dies
  in public.
* **Collation is a first-class input.** For `varchar` column vs `nvarchar` value:
  `SQL_*` collations → `ScanForced`; Windows collations → `RangeSeek`
  (`GetRangeThroughConvert` — cheaper than a scan, dearer than a seek), and
  benchmarked separately. Collation unknown and unpinned by the manifest →
  `Unknown`. Never guess silently.
* **Precedence matrix** encodes the official T-SQL list; seek/scan ground truth
  cross-checked against Kehayias' implicit conversion matrix (sqlskills.com) —
  cite it, but verify every pair we report against our own Docker oracle rather
  than trusting either source.
* **Literal typing:** `N'x'` nvarchar, `'x'` varchar, integer literal int, `1.5`
  numeric(p,s), date literals stay strings until compared.
* **Syntactic (Tier-1, no type info):** function-wrapped column, CAST/CONVERT on
  column, column arithmetic, leading-wildcard `LIKE`, non-literal `LIKE` pattern.
* **Hard cases** — explicit rule with fixtures, or `Unknown`: CASE/COALESCE/
  NULLIF result typing, mixed-type `IN` lists, `BETWEEN`, computed columns
  (persisted + indexed can still seek), `sql_variant`, date/time vs string.
* When inference is uncertain → `Unknown`, never a guess. `Unknown` and
  unanalyzable counts are reported honestly in the study.

## Dynamic SQL

`EXEC`/`sp_executesql` arguments are analyzed when they can be **proved
constant**, then run back through the normal pipeline with findings remapped to
their true source lines and the call site kept as provenance: literal or literal
concatenation; `sp_executesql`'s own params declaration for exact parameter
types; and reaching-definitions tracing of DECLARE/SET/SELECT chains through
straight-line code, recursing up to 5 levels. Anything not provably constant is
reported with a machine-readable reason and counted in `DynamicSqlSummary` —
never silently counted as clean. Soundness first: no heuristic string guessing.

## Verification and benchmarks

* **Oracle is plan-XML based, never plan-shape based.** A finding is confirmed
  iff the plan contains `CONVERT_IMPLICIT` applied to the COLUMN side of the
  predicate. Whether a tiny table happened to seek or scan is irrelevant.
* Per repo: deploy DDL to a fresh database → diff inferred view column
  types/collations against `sys.columns` (any mismatch is a P0 lineage bug) →
  for each `ScanForced` finding, submit a self-authored probe `SELECT` under
  `SET SHOWPLAN_XML ON` (compile-only, empty tables).
* Static verdicts never depend on the cardinality estimator — they state what
  the predicate makes possible for the engine. Benchmarks pin compat level 160
  and MAXDOP 1, and sweep both CE modes and both collation families so the
  numbers can't be dismissed. Median of 5 warm runs; CSV out.
* The study reports only oracle-confirmed findings; static-only findings go in
  an appendix.

## Corpus

`corpus/manifest.json` is checked in and pins repo URL, commit SHA, license, DDL
vs proc paths, and declared/assumed collation. Only repos whose SQL is plausibly
SQL Server (GO separators, bracket quoting, `dbo.`, ScriptDOM parse success
≥ 90%); files failing dialect sniffing are skipped — a MySQL file parsed as
T-SQL is noise. Do not invent our own corpus or hand-write repros: rule fixtures
come from real, internet-sourced implicit-conversion bugs.

Ethics: aggregate stats are public, no maintainer outreach required, no GitHub
issues or PRs filed on scanned repos, never name-and-shame in tone. Nothing gets
published externally without Umang's explicit go-ahead.

## Working agreements

* **Correct on the first pass.** No placeholders, dummy values, or TODOs left to
  clean up later. Leaving an edge case unimplemented is fine; faking it is not.
* **Tests:** xUnit; fixtures in `tests/SilentScan.Tests/fixtures/`, named
  `RULEID_fires.sql` / `RULEID_clean.sql`. Every rule ships a fixture that MUST
  fire, a near-miss that MUST NOT, and — if verdict-bearing — an oracle test.
  Keep a real balance of unit and integration tests. Fixtures must be repeatable
  and clean up unconditionally; no flaky state across runs. Aim for 99% coverage.
* **Zero issues, every category.** `dotnet build` (warnings are errors) and
  `dotnet test` clean, and a Sonar scan at 0 issues, before every commit —
  via `sonar-scan.ps1` then `sonar-check-issues.sh`.
* Deterministic output ordering. No network calls in Core. Findings schema is
  versioned JSON; SARIF export doubles the tool as a CI gate later.
* **Git:** conventional commits, authored as Umang Bhatt
  <bhatt.umang7@gmail.com>. Never credit Claude or any other model/company as
  co-author. Write commit messages about what changed and why — never
  "resolve item #3", which means nothing to someone reading it back later.
