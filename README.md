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

## What SilentScan detects

The themed summary above groups 49 finding streams into 7 families. This is
the same list broken down issue by issue — what's actually wrong, in plain
terms, for every distinct thing the tool looks for.

**Type/collation-aware conversion and write-loss**
* A predicate compares a lower-precedence-typed column against a
  higher-precedence value/param (e.g. `varchar` column vs `nvarchar` value)
  — T-SQL converts the *column*, not the value, silently losing the index
  seek. Direction-aware: the same mismatch the other way round is harmless.
* Collation family changes the verdict for the same mismatch: `SQL_*`
  collations force a full scan, Windows collations degrade to a cheaper
  (but not free) range seek.
* `sql_variant` compared against a normal type — same precedence logic
  applies, oracle-confirmed in both directions.
* A bounded-length column compared against a `MAX`-typed value/param, or
  against an oversized/under-length/undersized (declared length 1–2)
  parameter or variable — each a distinct silent cost or truncation risk,
  scored differently (seek-preserving-but-costlier vs. actually-truncating).
* Cross-table same-name column type/collation drift, and a value passed
  into a procedure's own parameter at a call site with a different declared
  type than the parameter itself.
* `ISNULL`/`COALESCE` against a nullable-vs-not-null column, wrapped in a
  way that silently changes whether the optimizer can simplify it away.
* `ANSI_PADDING OFF` silently strips trailing blanks on insert, so a `LIKE`
  pattern with significant trailing whitespace can never match a
  non-padded column no matter what's actually stored.
* A non-schema-bound table-valued function's `RETURNS TABLE` string column
  with no explicit `COLLATE` — its correctness silently depends on
  whatever the database's own default collation happens to be.
* Silent DML data loss the engine raises **no error** for: Unicode
  characters replaced with `?` when written into a non-Unicode column,
  a `REAL`/`FLOAT` value's fractional part dropped into an exact integer
  column, `DECIMAL`/`NUMERIC` digits rounded away by a smaller target
  scale, and a `DATETIME`-family value's time-of-day silently dropped when
  written into a `DATE` column.
* Two string columns compared directly whose resolved collations are
  genuinely incompatible — a hard compile error (Msg 468), not a
  seek/scan question, but one this pass can point at the exact cause of.
* A column's own collation has drifted from the database's default
  collation (multi-tenant/migrated-database smell).
* An unsized `CAST`/`CONVERT` to a string type resolved to T-SQL's real
  30-character default instead of being silently mis-typed by this tool
  itself — a correctness fix to how every other rule above reasons about
  CAST/CONVERT expressions, not a finding of its own.

**Sargability and index shape**
* A column wrapped in a function, `CAST`/`CONVERT`, arithmetic, a
  leading-wildcard `LIKE`, or a non-literal `LIKE` pattern — the classic
  ways a predicate stops being seekable, each suppressed when a matching
  indexed computed column already absorbs the wrap.
* A date-part function (`YEAR`/`MONTH`/`DAY`/`DATEPART`/`DATEDIFF`/
  `DATEADD`/`DATENAME`) applied to a column instead of rewriting the
  literal side — forces a scan unconditionally.
* `UPPER`/`LOWER` wrapping a column — forces a scan regardless of
  collation case-sensitivity, contrary to the common assumption that a
  case-insensitive collation makes the wrap harmless to remove.
* `CHARINDEX(x, col) = 1` / `LEFT(col, n) = 'x'` written instead of the
  exactly-equivalent, genuinely sargable `col LIKE 'x%'`.
* An end-of-period `BETWEEN` boundary literal with fewer fractional-second
  digits than the column's own declared precision — silently **excludes**
  real rows in the precision gap (a correctness bug, not just a plan one).
* A composite index's non-leading key column is constrained by a predicate
  while its own leading column is left completely unconstrained anywhere
  in the same statement — the b-tree structurally can't be searched by a
  suffix, and no other usable index covers the gap.
* An `INDEX(...)` hint naming an index that no longer exists, or pinning
  a real index the query's own predicates can't actually seek through —
  forcing the wrong, hint-pinned access path instead of letting the
  optimizer route around it.
* `TOP(100) PERCENT ... ORDER BY` inside a view or inline TVF — provably
  never limits anything and never guarantees the output order a consumer
  sees; even a genuinely row-limiting `TOP (n) ... ORDER BY` there only
  ever gets a lucky, plan-dependent illusion of order, never a guarantee.
* A window function's `OVER` clause using (explicitly, or by the silent
  default when unwritten) a `RANGE` frame instead of `ROWS` — measurably
  more expensive for identical peer-group semantics.

**Lineage-metric findings** (the numbers no other static tool can compute,
because they require resolving views/TVFs through the lineage pass first)
* A view/inline TVF nested 2+ layers deep over other views/TVFs — each
  layer is a place a `SELECT *`/column-mismatch/type-widening can hide.
* The *expanded* base-table join width after resolving every view/TVF in
  the FROM/JOIN list, vs. the written width — a query that reads like a
  3-table join but expands to 20 real tables is the finding nobody else
  can produce, ranked by the size of that gap.
* A CTE referenced 2+ times downstream of its own `WITH` clause — SQL
  Server re-runs the CTE's defining query independently per reference,
  never materializes and reuses it once.
* `SELECT *` inside a view/inline TVF whose column list is frozen at
  create time and demonstrably disagrees with the base table after a
  later `ALTER` — narrowed to only fire when a real consumer elsewhere in
  the corpus already selects a strict, named subset of the columns.

**Catalog and constraint findings**
* An untrusted (`WITH NOCHECK`) or disabled FK/CHECK constraint — the
  optimizer forfeits join elimination and other plan simplifications a
  trusted constraint would otherwise enable.
* A cascading `ON DELETE`/`ON UPDATE` FK action — hidden multi-table write
  work triggered by a single DML statement against the parent.
* A JOIN that equates only part of a composite foreign key, leaving the
  remaining column(s) uncovered anywhere else in the statement — silent
  row multiplication, not just a missed index.
* A non-persisted computed column — recomputed on every read, unlike its
  `PERSISTED` sibling.
* A `MAX`-typed column used as a predicate/join target — can never be an
  index key at all, by the engine's own `CREATE INDEX` rules.
* `SET QUOTED_IDENTIFIER OFF` / `NUMERIC_ROUNDABORT ON` / `ANSI_NULLS OFF`
  / `ANSI_WARNINGS OFF` / `CONCAT_NULL_YIELDS_NULL ON` on a module that
  actually touches a filtered index or indexed view — each one silently
  blocks the optimizer from using that plan feature at all, degrading a
  seek/match to a full scan.
* A system-versioned temporal table whose history table lacks an index
  the current table has — `FOR SYSTEM_TIME` queries rewrite to a UNION ALL
  between the two tables, so a sargable predicate does nothing for the
  history half.
* A procedure authored `WITH RECOMPILE` — compiles fresh on every single
  call, invisible to plan-cache-based monitoring.
* Database-level configuration smells read straight from `sys.databases`
  (the one finding category at database granularity, not module/column/
  predicate): `PAGE_VERIFY` not `CHECKSUM`, `AUTO_SHRINK` on, `AUTO_CLOSE`
  not off, `TARGET_RECOVERY_TIME` unset, and Query Store mode/capture
  settings out of the recommended range.
* A comma-join or explicit `CROSS JOIN` with no predicate anywhere in the
  statement connecting the two sides — a true, unqualified cartesian
  product.
* A `varchar`/`nvarchar`/`char`/`nchar`/`binary`/`varbinary` column or
  declaration of length 1 or 2 — almost always a truncated-from-a-larger-
  source mistake or a leftover placeholder, reported as advisory.

**Plan-shape and correctness findings**
* A multi-statement table-valued function referenced directly, via a
  correlated `CROSS`/`OUTER APPLY`, inherited through a view/iTVF layer, or
  as an `INSERT ... EXEC` target — each forces the optimizer to treat it as
  an opaque, uncosted black box (a "fence") no statistics can see through.
* A scalar UDF invoked in predicate position (WHERE/JOIN ON/HAVING/MERGE
  ON), in projection position, or referenced from a computed column/
  DEFAULT/CHECK constraint — each forces per-row evaluation and/or blocks
  parallelism, cross-checked against the engine's own 2019+ inlining
  verdict rather than a hand-maintained blocker list alone.
* Forced-serial constructs: a table variable as a DML target (that one
  statement's plan loses parallelism entirely), a `FAST_FORWARD` cursor
  (the option itself, not its absence, is what forces the cursor's own
  query serial — the opposite of the commonly repeated advice), and a
  short, finite list of oracle-confirmed non-parallelizable intrinsic
  functions referenced inside a query with a real FROM clause.
* `(col = @p OR @p IS NULL)` and its swapped/chained variants — the
  classic "optional filter" idiom that defeats cardinality sniffing,
  fully suppressed under `OPTION(RECOMPILE)`.
* A predicate against a `DECLARE`'d local variable, or against a formal
  parameter reassigned before its own predicate use — in both cases the
  optimizer's cached-plan cardinality estimate is provably blind to the
  value actually compared at runtime.
* `NOT IN (SELECT ...)` against a subquery column the catalog proves can
  be `NULL` — a genuine correctness bug (ANSI three-valued logic turns the
  whole predicate UNKNOWN the instant one `NULL` row exists), not a
  plan-shape one.
* `UPDATE ... FROM`/`DELETE ... FROM` joined to a source with no PK/unique
  constraint backing its own join columns — SQL Server picks an
  unspecified, plan-dependent row among the matches, silently.
* A DML statement (`INSERT`/`UPDATE`/`DELETE`/`MERGE`) whose own source
  query reads the same table it's writing to, directly or through a view
  — Halloween Protection, an extra defensive spool or sort the plan pays
  for on every execution.
* A `#temp` table populated by `SELECT INTO` and later used as a JOIN
  source or filtered with no index ever created against it.

**Control-flow and transaction correctness**
* `BEGIN TRANSACTION` with at least one code path (through IF/ELSE,
  TRY/CATCH, WHILE, early RETURN/THROW) that reaches the end of the batch
  without a matching `COMMIT`/`ROLLBACK` — locks held indefinitely on that
  path, confirmed against the real engine error (Msg 266) and elevated
  `@@TRANCOUNT`.
* `WAITFOR DELAY`/`WAITFOR TIME` inside a routine or batch — a blocked
  worker thread every time, flagged more sharply when it's reachable
  inside an already-open transaction (holding that transaction's locks for
  the same duration).
* `TRUNCATE TABLE` inside a `TRY` block whose `CATCH` swallows the error
  silently — `TRUNCATE` can fail (an enforced FK reference is the common
  case), and unlike an uncaught failure, nothing surfaces it.
* An `OUTPUT` parameter not guaranteed to be assigned on every return path
  through the procedure — the caller can read a stale or default value
  with no error raised.
* `SET DATEFORMAT`/`SET DATEFIRST` changed mid-module — changes how a date
  literal or `DATEPART`-relative comparison is parsed for the rest of that
  module's own execution, independent of the caller's own session
  settings.

**Dynamic SQL**
* `EXEC`/`sp_executesql` text that can be proven constant (literal
  concatenation, `sp_executesql`'s own typed `@params`, or straight-line
  reaching-definitions tracing of `DECLARE`/`SET`/`SELECT` chains) is
  re-run through the entire pipeline above, with every finding remapped
  back to its true source line and the call site kept as provenance.
* A value (as opposed to an identifier) spliced into otherwise-constant
  dynamic SQL text via string concatenation instead of a real
  `sp_executesql` parameter — measured to pollute the plan cache with one
  distinct cached plan per distinct value, where a real parameter compiles
  exactly one.
* `EXEC(string)` used where `sp_executesql` with real parameters was
  available and simply wasn't used.
* `INSERT INTO #temp EXEC OtherProc` where the executed procedure's real,
  engine-described result set (`sys.dm_exec_describe_first_result_set`,
  compile-only) doesn't match `#temp`'s own declared columns by position —
  either a hard runtime error (column count) or a silent conversion/
  truncation (column type), both invisible to file-only analysis.

Every claim above is oracle-verified against a real SQL Server instance
where it's a plan-shape or runtime behavior claim, never assumed from
documentation or folklore — several items above are stated the way they
are specifically *because* the commonly repeated version turned out wrong
under direct testing (the `FAST_FORWARD` cursor and `UPPER`/`LOWER`
case-folding items are two examples). Full mechanism, precision guards,
scope limits, and real measured coverage for every stream are in
`docs/detection-checklist.md`.

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
