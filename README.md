# SilentScan

Static analyzer for SQL Server code. If a defect is detectable from the code
and schema — via an engine-authoritative catalog, a lineage pass, a plan-XML
oracle, or plain syntax — it's in scope; if it only shows up once the app is
running in production, it's out. 49 finding streams as of this writing,
ranging from type-aware, direction-aware, collation-aware implicit-conversion
detection (the tool's original differentiator, still the template for how
oracle-backed rules ship) down to cheap, high-precision syntactic checks.
Grouped by theme:

* **Type/collation-aware conversion and write-loss family** — implicit
  conversions that kill an index seek (column-side vs value-side,
  collation-aware, `RangeSeek` vs `ScanForced`, inherited through
  views/iTVFs, dynamic-SQL constant tracing); silent DML data loss with no
  engine error (unicode-to-non-unicode, approximate-to-exact truncation,
  numeric scale narrowing, temporal precision loss); collation conflicts
  (Msg 468); `sql_variant` comparisons; oversized/`MAX`-typed, under-length,
  and undersized (declared length 1–2) parameter/column pairings;
  cross-table and call-boundary type drift; `ANSI_PADDING` comparison-seed
  mismatches; a database-collation-dependent non-schema-bound TVF return
  shape; an explicit-length fix so `CAST`/`CONVERT` to an unsized string
  type resolves T-SQL's real 30-character default instead of guessing.
* **Sargability and index-shape (syntactic + type-aware)** — function-wrapped
  columns, CAST/CONVERT-on-column, column arithmetic, leading-wildcard
  `LIKE`, non-literal `LIKE` patterns, date-form functions, case-folding,
  `CHARINDEX`/`LEFT` rewrite candidates, `ISNULL`/`COALESCE` nullability
  cases, a temporal-boundary correctness check (an end-of-period `BETWEEN`
  literal that silently excludes rows in a precision gap), a composite
  index's non-leading key column constrained while its leading column is
  wide open, an `INDEX(...)` hint naming a nonexistent or unseekable index,
  `TOP(100) PERCENT`/`ORDER BY` inside a view or inline TVF (which never
  guarantees output order to the consumer), and an `OVER` clause's implicit
  or explicit `RANGE` frame (measurably costlier than the equivalent `ROWS`
  frame).
* **Lineage-metric findings** — nested-view depth, post-expansion join
  width, CTEs referenced 2+ times downstream of their own `WITH` clause,
  and `SELECT *` inside a view/inline TVF narrowed by a real consumer.
* **Catalog and constraint findings** — untrusted/disabled FK and CHECK
  constraints, cascading FK delete/update actions, joins matching only part
  of a composite FK, non-persisted computed columns, `MAX`-typed predicate
  columns, `SET` options that silently disable indexed-view/filtered-index
  plan features, column-collation drift from the database default, a
  system-versioned temporal table whose history side lacks the current
  table's own index set, `WITH RECOMPILE`-authored procs, unhealthy
  database-level configuration flags (`PAGE_VERIFY`, `AUTO_SHRINK`,
  `AUTO_CLOSE`, `TARGET_RECOVERY_TIME`, Query Store mode — the one
  database-granularity finding category in the tool), and a comma-join or
  `CROSS JOIN` with no predicate connecting the two sides anywhere in the
  statement.
* **Plan-shape and correctness findings** — MSTVF-as-fence references
  (direct, correlated `CROSS/OUTER APPLY`, inherited through a view/iTVF,
  `INSERT ... EXEC`); scalar UDF cost (predicate/projection contexts, plan
  inlineability — cross-checked against the engine's own `is_inlineable`
  verdict, schema-level dependencies); forced-serial constructs (table
  variables, `FAST_FORWARD` cursors, non-parallelizable intrinsics);
  catch-all/kitchen-sink predicates and predicates against a formal
  parameter reassigned before use (both defeat cached-plan cardinality
  sniffing, both suppressed under `OPTION(RECOMPILE)`); `NOT IN` over a
  nullable subquery column; `UPDATE ... FROM` with no source-side uniqueness
  guarantee; self-referencing DML risking Halloween Protection (an extra
  blocking operator when a DML statement's source reads the table it's
  writing to); an unindexed `#temp` table later joined or filtered.
* **Control-flow and transaction correctness** — `BEGIN TRANSACTION` with no
  guaranteed `COMMIT`/`ROLLBACK` on some code path; `WAITFOR DELAY`/`WAITFOR
  TIME` (flagged more sharply when it holds an already-open transaction's
  locks); `TRUNCATE TABLE` whose failure a surrounding `TRY`/`CATCH` swallows
  silently; an `OUTPUT` parameter not guaranteed to be assigned on every
  return path; `SET DATEFORMAT`/`SET DATEFIRST` changed mid-module, which
  changes how a date literal or `DATEPART`-relative comparison is parsed for
  the rest of that module's own execution.
* **Dynamic SQL** — proven-constant `EXEC`/`sp_executesql` text re-run
  through the full pipeline with findings remapped to their true source;
  concatenated values that should have been parameterized; `EXEC(string)`
  where `sp_executesql` was available and unused; temp-table shape mismatch
  across an `INSERT ... EXEC` proc-call boundary
  (`sys.dm_exec_describe_first_result_set`-backed, live-mode only).

Plus a corpus study pipeline that quantifies prevalence and cost across
public SQL Server codebases.

Parsing is via `Microsoft.SqlServer.TransactSql.ScriptDom` (SQL Server /
T-SQL only, no other dialects). Findings are oracle-verified against real
query plans wherever a plan-shape or runtime claim is made, never guessed
from plan shape; a purely structural/catalog fact needs no oracle. Full
detail, scope decisions, and precision guards for every stream are in
`docs/detection-checklist.md` (working backlog) and `CLAUDE.md` (project
contract).

## Requirements

* .NET 10 SDK
* Docker (for the verification oracle and benchmarks)

## Build & test

```
scripts/dotnet-safe.sh build
scripts/dotnet-safe.sh test
```

A thin wrapper around `dotnet build`/`dotnet test` that disables MSBuild node
reuse and reaps stray build-server processes — plain `dotnet build`/`dotnet
test` can hang or crash on a known VBCSCompiler race under repeated
invocations; see the script's own header comment for the reproduced failure
modes. `Directory.Build.props` treats warnings as errors solution-wide.

`tests/SilentScan.Tests/Integration/` requires the Docker SQL Server below to
be running — there is no mock/skip path.

## Scan a live database

```
dotnet run --project src/SilentScan.Cli -- scan-db <connection-string> [--format text|markdown|json|sarif] [--confidence high|medium|low] [--plan-cache-evidence] [--fetch-sql-from-tables] [--verbosity brief|full] [--output <file>]
```

Connects to a live SQL Server database, reads its catalog directly from
engine metadata (`sys.tables`/`sys.columns`/`sys.indexes`/`sys.sql_modules`
and more) rather than inferring it from parsed DDL text, and runs every
readable module (views/procs/functions/triggers) through the full detection
pipeline — all 49 finding streams, in one pass. Types, per-column
collations, and the indexed flag are all facts read from the engine, never
guessed. Read-only by design (`LiveReadOnlyGuard`): only `SELECT`s are
issued, plus one narrow, explicitly-scoped exception — a compile-only
`sys.dm_exec_describe_first_result_set` probe (never executes the batch it's
handed) against either a bare `SELECT` or a bare named-procedure `EXEC`, used
only for the temp-table-shape-mismatch stream. No DDL or DML is ever
executed against the connected database; with `--fetch-sql-from-tables`,
some of those `SELECT`s read real row content (dynamic SQL text stored in a
table), still read-only.
Output defaults to `--format text`: a readable report that groups findings by
what is wrong with them, explains each group once, and gives every finding its
location, base column, indexed flag and the view/TVF layer that introduced
the defect. `--format markdown` is the same report as a shareable document,
`--format json` is the complete versioned findings schema, and `--format sarif`
emits SARIF for CI gating. `--output <file>` writes the report to a file
instead of standard output.

## Corpus study pipeline

`corpus/manifest.json` pins the repo set (URL, commit SHA, license, DDL/proc
paths, declared collation). Clone the pinned repos into `corpus/_clones/`
first, then:

```
dotnet run --project src/SilentScan.Cli -- scan-corpus-live [--manifest corpus/manifest.json] [--clones-root corpus/_clones] [--format text|markdown|json|sarif] [--confidence high|medium|low] [--verbosity brief|full] [--output <file>]
```

Deploys every manifest repo's DDL to the disposable Docker oracle, reads its
catalog and module text back from the engine (never parses repo DDL text
directly — "everything goes via the database"), and reports per-repo
findings across every stream (the same pipeline `scan-db` runs), gated on
each repo's ScriptDOM parse-health passing the dialect-sniffing bar. The
readable formats lead with a one-row-per-repo rollup table — findings, parse
rate, dialect-sniffing result — followed by each repo's full report.

```
dotnet run --project src/SilentScan.Verify -- verify-corpus [--manifest corpus/manifest.json] [--clones-root corpus/_clones] [--repo <name>]
```

Deploys each repo's DDL to a fresh disposable database, diffs inferred
view/TVF column types and collations against `sys.columns` (or, for a
view/inline TVF, `sys.dm_exec_describe_first_result_set`'s live answer),
and oracle-confirms the original three streams' findings with a compile-only
`SET SHOWPLAN_XML ON` probe: `CONVERT_IMPLICIT` on the column side of a
predicate for implicit-conversion findings, a `Table-valued function`
plan operator (or `INSERT EXEC` statement type) for MSTVF-as-fence findings,
and a `UserDefinedFunction` plan element (cross-checked against the engine's
own inlining behavior) for scalar UDF findings. Every stream shipped since
carries its own oracle test suite (xUnit, against the same Docker instance)
rather than being folded into this specific per-repo CLI pipeline — see
`docs/detection-checklist.md` for each stream's own oracle mechanism.
Corpus DML and stored procedures are never executed — only self-authored
probes run against the disposable database.

## Confidence tiers and inference rules

Every finding carries a `Confidence`: `High`, `Medium`, or `Low`. Most
statically-derived findings — anything provable from ordinary parsed SQL
text as a structural or plan-shape fact — report `High`. `scan-db` and
`scan-corpus-live` both accept `--confidence high|medium|low` (default
`high`) to control the least confident a finding may be and still appear in
the report; SARIF output maps a below-`High` finding to level `note` and
gives it a `/medium-confidence`- or `/low-confidence`-suffixed rule ID,
independently filterable from its `High` counterpart.

`Medium` covers findings the tool can prove exist but can't fully vouch for
in isolation: a dynamic-SQL finding derived from a value the folder could
only assume — a placeholder standing in for a variable it could prove had a
type but not a value (an uninitialized `DECLARE`, a proc parameter with no
known caller); `PartialCompositeForeignKeyJoinFinding` (a JOIN matching only
part of a composite foreign key, which can also be a genuine, deliberate
fan-out); and `UnindexedTempTableUsageFinding` (SQL Server's own automatic
temp-table statistics can make a small, short-lived `#temp` table cheap to
scan regardless of an index).

`Low` covers findings that are real but carry no magnitude claim — the tool
can state the optimizer-visible fact but not whether it costs anything in a
given case: predicates against a `DECLARE`d local or a formal parameter
reassigned before use (the optimizer's cardinality estimate is provably
blind to the compared value, but whether that produces a bad plan depends on
data this pass can't see), `SET DATEFORMAT`/`SET DATEFIRST` changed
mid-module, a declared type of length 1 or 2, and the weaker half of the
view-ordering finding (`ORDER BY` inside a view/TVF not guaranteed to reach
the consumer, as opposed to the `TOP(100) PERCENT` case, which is
provably meaningless and stays `High`).

## Verification oracle (Docker SQL Server)

```
cp .env.example .env      # override SILENTSCAN_SA_PASSWORD if you want
docker compose up -d
```

Connects on `localhost,14330`, user `sa`. Backs `SilentScan.Verify` and
`SilentScan.Bench`. Compat level 160 is pinned per-database by the tooling
after `CREATE DATABASE`, not at the server level.

## Benchmarks

```
dotnet run --project src/SilentScan.Bench -- run \
  --rows 10000 1000000 10000000 \
  --output silentscan-bench-results.csv
```

Runs the seek/range-seek/scan cost matrix (type pairs × row counts × legacy/new
cardinality estimator × matched/mismatched param) against a fresh disposable
database and writes a CSV. Median of 5 warm runs per cell.

## Sonar

```
pwsh ./sonar-scan.ps1              # build + test + coverage + upload + wait + print result
pwsh ./sonar-scan.ps1 -Verbose     # same, with full scan/build/test output as it runs
```

## Layout

* `src/SilentScan.Core` — the four analysis passes: `Parsing/`, `Catalog/`
  (tables, columns, types, collations, indexes, check constraints, scalar
  UDF/TVF metadata), `Lineage/` (view/TVF resolution, topo order, column
  provenance, TVF-fence/scalar-UDF/view-expansion maps), `Predicates/` +
  `Rules/` (every scanner and finding type, extraction, precedence,
  verdicts), `Reporting/` (JSON + SARIF + the readable report), `Corpus/`.
* `src/SilentScan.Live` — the engine-authoritative catalog reader
  (`sys.tables`/`sys.columns`/`sys.indexes`/`sys.sql_modules`/foreign keys/
  check constraints/indexed views and more) and live-mode-only scanners
  (e.g. plan-cache evidence, `sys.dm_exec_describe_first_result_set` probes)
  that need a real connected database rather than parsed DDL.
* `src/SilentScan.Cli` — `scan-db` / `scan-corpus-live` commands.
* `src/SilentScan.Verify` — Docker-backed oracle: DDL deployment,
  `sys.columns` diffing, and plan-XML confirmation for the original three
  streams (`verify-corpus`, `generate-type-matrix`); `LiveReadOnlyGuard`,
  the read-only enforcement every live query goes through.
* `src/SilentScan.Bench` — the benchmark harness.
* `tests/SilentScan.Tests` — xUnit tests and fixtures.

See `CLAUDE.md` for the full type-rule specification and project contract,
`docs/detection-checklist.md` for the complete, gated list of what's shipped
and what's next (mechanism, scope decisions, and real coverage numbers for
every stream), and `docs/local-dev.md` for further local-dev detail.
