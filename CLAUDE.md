# CLAUDE.md — SilentScan

Static analyser for SQL Server code.

**Scope: if it's detectable from the code and schema, it's in scope. If it
only shows up once the app is running in production, it's out** — we are not
an application performance monitor. That's the whole rule.

Type-aware, direction-aware, lineage-aware analysis (engine-authoritative
catalog, lineage pass, plan-XML oracle) is our differentiator and stays the
template for how oracle-backed rules ship. Syntax-only rules ship too, same
finding schema, fire/near-miss fixtures instead of an oracle.

Working backlog: `docs/detection-checklist.md`. Research behind it:
`docs/detection-reference.md`.

**Tool-first.** The tool (`silentscan scan`/`scan-db`/`scan-corpus`/
`verify-corpus`, JSON + SARIF findings) is the deliverable, and the primary
target driving what gets built is the local test database — real
production-grade T-SQL, 100%
parse/detect coverage of it is the bar. The corpus **study** (prevalence/cost
numbers across public repos) is a later deliverable: when it's actually about
to be published, run the full scan-corpus/verify-corpus/bench pipeline fresh
and write the narrative from those live numbers — never maintain it as a
standing checked-in file and never hand-edit a previously-written paragraph
(`docs/study.md` drifted stale exactly that way and was deleted for it).
Fame goal, not commercial. **Precision beats recall everywhere** — one false
positive in a published finding is worse than ten missed true positives.

**Standing docs** — exactly these, updated in place, read fresh each session:

* `CLAUDE.md` — this contract: current rules, not point-in-time status.
* `docs/detection-checklist.md` — the working backlog. A gated checkbox list
  (work items one by one, check off, prune sections) is deliberately the one
  sanctioned "plan file": unlike the old narrative `plan.md`, it states
  decisions and gates rather than prose-with-numbers, so it doesn't rot the
  same way. Keep it honest: check items off when shipped, move re-litigated
  decisions with their reasons, delete sections that stop being useful.
* `docs/detection-reference.md` — the detection-space research record, so no
  surveyed fact has to be re-researched.
* `docs/local-dev.md` — local setup.

Any other markdown is disposable by default: generate it for the session's
purpose and delete it when the work is done. Don't create new standing files
this contract has to remember to keep alive.

When you have to make code changes and are just moving to the next stage and all that you have to ask from me is yes, never stop for me in such situations - just continue working. If you catch yourself about to ask "should I continue?" or "want me to proceed to the next item?" - don't ask, just continue, and mention what you're doing as you go. This applies even when a nearby step (e.g. committing) genuinely does need to wait for a go-ahead: don't let that one blocked step stop the rest of the work - keep going on everything that isn't itself blocked, and surface the blocked step in passing rather than pausing the whole turn on it.

# remote database
When working with a remote database, make sure that no information about the schema of the remote database is leaked into the tests or comments or commit messages or any other place.

## Hard scope (do not revisit without asking)

* SQL Server / T-SQL only. Parser is `Microsoft.SqlServer.TransactSql.ScriptDom`.
  No other dialects, no ANTLR.
* EF Core / ORM analysis is out of scope. SQL text only.
* Scanned-target DML and procs are **never executed**, anywhere — not corpus
  code, not production-copy code. The only execution is self-authored probes
  inside the disposable Docker SQL Server.
* A connected live database (`scan-db`) is scanned **read-only**: only
  `SELECT` queries are issued (never DDL/DML), enforced in code
  (`LiveReadOnlyGuard`) rather than left to review alone - the target is never
  written to, altered, or executed against beyond that. `scan-db` targets a
  development/staging database the user is actively debugging with this tool,
  not an untrusted or production target read blind - so unlike the earlier,
  stricter draft of this rule, reading actual row content (not just catalog
  metadata) is permitted when a feature genuinely needs it, e.g. resolving
  dynamic SQL text stored in a table (`SELECT @sql = Definition FROM
  dbo.Templates WHERE ...`) so it can be analyzed like any other dynamic SQL
  source. `sys.dm_exec_describe_first_result_set` remains a separate,
  narrower guarantee worth keeping precise: it parses, binds, and compiles the
  batch text it's handed and returns result-set metadata **without executing
  it at all** - no rows returned, compile-only, same principle as the Verify
  oracle's `SET SHOWPLAN_XML` probes. The probe text handed to it is itself
  asserted SELECT-only by `LiveReadOnlyGuard` before being bound as a
  parameter. Any reader that DOES fetch real row content is a separate,
  explicit code path from the describe-only probe, SELECT-only enforced the
  same way, but not compile-only.
* .NET 10, C#. Ubuntu; Docker assumed available.
* Corpus stays at the pinned 5-repo pilot set unless we decide otherwise.
* **Everything goes via the database — no file-parsed catalog, no file-only
  scan.** Schema truth AND module text come from a real SQL Server: `scan-db`
  reads a live target; corpus scanning deploys the repo's (whitelist-filtered)
  DDL to the disposable Docker instance, then reads the catalog
  (`LiveCatalogReader`) and module text (`sys.sql_modules`) back out. Repo
  files are read for exactly one purpose: deployment. Do NOT invest in
  file-parsed DDL→catalog fidelity (statement ordering, ALTER merge, SELECT
  INTO, computed-column typing, …) — replicating the engine's DDL semantics is
  reinventing the database-project wheel. The only parser-derived catalog data
  is module-body objects the engine can't expose: temp tables created inside
  proc bodies, table variables, TVPs, MSTVF return shapes. Corpus findings
  still map back to the defining repo file, since the study cites repos.

## New detection streams (the checklist's cross-cutting rules, binding here)

* Whether a rule needs the catalog/lineage/oracle just decides its schema
  (verdict + oracle fixture) vs. a syntax-only one (fire/near-miss fixtures).
  It's not an admission gate — see the scope rule at the top of this file.
* Findings in every stream carry the conversion stream's schema: `verdict`,
  whether the base column is **indexed**, **depth** (0 = direct table
  predicate, N = view/TVF layers between predicate and base column), and
  **origin** — both the predicate's file/line and the file/line of the layer
  that introduced the problem. Rank indexed + depth ≥ 1 first; lineage-depth
  prevalence is the number nobody else can produce.
* Every rule states its engine-version sensitivity (2017 interleaved
  execution, 2019 UDF inlining, 2022 CE behavior) rather than assuming one.
* Verdict-bearing rules ship an oracle fixture; syntactic-only rules ship
  fire + near-miss fixtures from real, internet-sourced bugs.

## Layout

`src/SilentScan.Core` holds the four passes — `Parsing/`, `Catalog/` (tables,
columns, types, collations, indexes), `Lineage/` (view/iTVF resolution, topo
order, column provenance), `Predicates/` + `Rules/` (extraction, precedence,
verdicts), `Reporting/` (ranked findings, JSON + SARIF), plus `Corpus/`.
Then `SilentScan.Cli`, `SilentScan.Verify` (Docker oracle), `SilentScan.Bench`,
and `tests/SilentScan.Tests`.

`Verdict` is `SeekPreserved | RangeSeek | ScanForced | Unknown`; syntactic
non-sargability is its own finding stream (`SargabilityFindingKind`).

## The conversion type rules (shipped stream — the template; keep exactly right)

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
  than trusting either source. (This oracle check is always available and never
  needs anyone's go-ahead — the Docker instance is standing infrastructure, not
  a gated resource. Run it whenever a rule's real behavior is in question;
  never leave a change unverified or delay work waiting for permission to check.)
* **Literal typing:** `N'x'` nvarchar, `'x'` varchar, integer literal int, `1.5`
  numeric(p,s), date literals stay strings until compared.
* **Syntactic (Tier-1, no type info):** function-wrapped column, CAST/CONVERT on
  column, column arithmetic, leading-wildcard `LIKE`, non-literal `LIKE` pattern.
* **Hard cases** — explicit rule with fixtures, or `Unknown`: CASE/COALESCE/
  NULLIF result typing, mixed-type `IN` lists, `BETWEEN`, computed columns
  (persisted + indexed can still seek), `sql_variant`, date/time vs string.
* When inference is uncertain → `Unknown`, never a guess. `Unknown` and
  unanalyzable counts are reported honestly wherever results are published.

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

* **Oracle is plan-XML based, never plan-shape based.** A conversion finding is
  confirmed iff the plan contains `CONVERT_IMPLICIT` applied to the COLUMN side
  of the predicate; each new stream defines its own equally-specific plan-XML
  marker (the checklist records them). Whether a tiny table happened to seek
  or scan is irrelevant.
* Per repo: deploy DDL to a fresh database → diff inferred view column
  types/collations against `sys.columns` (any mismatch is a P0 lineage bug) →
  for each `ScanForced` finding, submit a self-authored probe `SELECT` under
  `SET SHOWPLAN_XML ON` (compile-only, empty tables). This plain `sys.columns`
  diff is sound here specifically because the DDL was just deployed and
  nothing has been alter'd since - staleness is structurally impossible in a
  freshly-provisioned disposable database.
* `scan-db`'s own lineage parity gate cannot make that assumption - a live
  target may have had a base column retyped years after a view over it was
  last created or altered, and SQL Server never refreshes a view's/inline
  TVF's own cached column metadata when that happens (short of
  `sp_refreshview`/`sp_refreshsqlmodule`). So for a view or inline TVF, ground
  truth is what the engine computes for that object **right now**
  (`sys.dm_exec_describe_first_result_set`), never its cached `sys.columns`
  row. A disagreement with the live answer is a P0 lineage bug and fails the
  scan. An object the server can no longer compile at all, and an object
  whose cached metadata has merely drifted from a live answer this tool's own
  inference agrees with, are conditions of the scanned database, not bugs in
  this tool - both are reported prominently but neither fails the scan. Base
  tables and multi-statement TVFs keep the plain `sys.columns`/authored-shape
  diff: a base table's `sys.columns` *is* its definition, and a
  multi-statement TVF's shape is its own authored `RETURNS @t TABLE(...)`
  clause, so staleness cannot occur for either.
* Static verdicts never depend on the cardinality estimator — they state what
  the predicate makes possible for the engine. Benchmarks pin compat level 160
  and MAXDOP 1, and sweep both CE modes and both collation families so the
  numbers can't be dismissed. Median of 5 warm runs; CSV out.
* The study reports only oracle-confirmed findings; static-only findings go in
  an appendix.

## Corpus

`corpus/manifest.json` is checked in and pins repo URL, commit SHA, license, DDL
vs proc paths, and declared/assumed collation. Only repos whose SQL is plausibly
SQL Server (GO separators, bracket quoting, `dbo.`) are curated into the manifest
in the first place. ScriptDOM parse success ≥ 90% of a repo's files is the
dialect-sniffing bar (`ParseHealthReport.PassesDialectSniffing`) both
`scan-corpus` and `verify-corpus` check per repo: a repo that falls below it
gets a loud warning and a non-zero exit code (findings are still reported,
never silently dropped) rather than scanning as if it were clean — a MySQL
file parsed as T-SQL is noise, and this is what catches it. Do not invent our
own corpus or hand-write repros: rule fixtures come from real, internet-sourced
bugs.

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
  and clean up unconditionally; no flaky state across runs. No numeric coverage
  target — the bar is that every rule's fire/near-miss/oracle triple exists and
  asserts the real thing, not coverage for its own sake.
* **Quality gates:** `dotnet build` (warnings are errors) and `dotnet test`
  clean before every commit. The Sonar scan at 0 issues (`sonar-scan.ps1` —
  scans, waits for processing, prints the result; `-Verbose` for full output)
  runs when a logical unit of work lands — a stream/feature finishing, or
  before anything is pushed or published — rather than gating every
  intermediate commit.
* Deterministic output ordering. No network calls in Core. Findings schema is
  versioned JSON; SARIF export doubles the tool as a CI gate later.
* **Git:** conventional commits, authored as Umang Bhatt
  <bhatt.umang7@gmail.com>. Never credit Claude or any other model/company as
  co-author. Write commit messages about what changed and why — never
  "resolve item #3", which means nothing to someone reading it back later. Do not
  use "Phase 1", "gate 1" etc place holders in the commit message that have
  no meaning in the future. Commit after each logical work unit finishes.

# Build and test
Never run `dotnet test` or `dotnet build` directly - always go via dotnet-safe.sh.
```
scripts/dotnet-safe.sh build
scripts/dotnet-safe.sh test
scripts/dotnet-safe.sh test --filter "FullyQualifiedName~DynamicSql"
```


