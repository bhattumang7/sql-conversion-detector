# SilentScan

Static analyzer for SQL Server query-level performance defects that only an
engine-authoritative catalog, a lineage pass, or a plan-XML oracle can detect
precisely — the ones no purely syntactic linter can see:

* **Implicit conversions** that kill an index seek — column-side vs
  value-side, collation-aware, including ones inherited through layers of
  views and inline table-valued functions.
* **MSTVF-as-fence references** — a multi-statement or CLR table-valued
  function opaque to the optimizer, reached directly, through a correlated
  `CROSS/OUTER APPLY`, inherited invisibly through a view/iTVF layer, or via
  `INSERT ... EXEC`.
* **Scalar UDF cost** — per-row execution, non-sargable predicates, and
  (pre-2019, or when the engine proves the UDF non-inlineable) a forced-serial
  plan; reached directly, through view/iTVF lineage, or from a computed
  column/DEFAULT/CHECK constraint.

Plus a corpus study pipeline that quantifies prevalence and cost across
public SQL Server codebases.

Parsing is via `Microsoft.SqlServer.TransactSql.ScriptDom` (SQL Server /
T-SQL only, no other dialects). Findings are oracle-verified against real
query plans, never guessed from plan shape.

## Requirements

* .NET 10 SDK
* Docker (for the verification oracle and benchmarks)

## Build & test

```
dotnet build
dotnet test
```

`Directory.Build.props` treats warnings as errors solution-wide.

`tests/SilentScan.Tests/Integration/` requires the Docker SQL Server below to
be running — there is no mock/skip path.

## Scan a live database

```
dotnet run --project src/SilentScan.Cli -- scan-db <connection-string> [--format text|markdown|json|sarif] [--confidence high|medium] [--plan-cache-evidence] [--fetch-sql-from-tables] [--verbosity brief|full] [--output <file>]
```

Connects to a live SQL Server database, reads its catalog directly from
engine metadata (`sys.tables`/`sys.columns`/`sys.indexes`/`sys.sql_modules`)
rather than inferring it from parsed DDL text, and runs every readable
module (views/procs/functions/triggers) through the full detection pipeline
— implicit conversions, MSTVF-as-fence references, and scalar UDF cost, all
in one pass. Types, per-column collations, and the indexed flag are all
facts read from the engine, never guessed. Issues `SELECT`s only — no DDL or
DML is ever executed against the connected database; with
`--fetch-sql-from-tables`, some of those `SELECT`s read real row content
(dynamic SQL text stored in a table), still read-only.
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
dotnet run --project src/SilentScan.Cli -- scan-corpus-live [--manifest corpus/manifest.json] [--clones-root corpus/_clones] [--format text|markdown|json|sarif] [--confidence high|medium] [--verbosity brief|full] [--output <file>]
```

Deploys every manifest repo's DDL to the disposable Docker oracle, reads its
catalog and module text back from the engine (never parses repo DDL text
directly — "everything goes via the database"), and reports per-repo
findings across all three streams, gated on each repo's ScriptDOM parse-health
passing the dialect-sniffing bar. The readable formats lead with a
one-row-per-repo rollup table — findings, parse rate, dialect-sniffing result
— followed by each repo's full report.

```
dotnet run --project src/SilentScan.Verify -- verify-corpus [--manifest corpus/manifest.json] [--clones-root corpus/_clones] [--repo <name>]
```

Deploys each repo's DDL to a fresh disposable database, diffs inferred
view/TVF column types and collations against `sys.columns` (or, for a
view/inline TVF, `sys.dm_exec_describe_first_result_set`'s live answer),
and oracle-confirms every stream's findings with a compile-only
`SET SHOWPLAN_XML ON` probe: `CONVERT_IMPLICIT` on the column side of a
predicate for implicit-conversion findings, a `Table-valued function`
plan operator (or `INSERT EXEC` statement type) for MSTVF-as-fence findings,
and a `UserDefinedFunction` plan element (cross-checked against the engine's
own inlining behavior) for scalar UDF findings.
Corpus DML and stored procedures are never executed — only self-authored
probes run against the disposable database.

## Confidence tiers and inference rules

Every finding carries a `Confidence`: `High`, `Medium`, or `Low`. A
statically-derived finding — anything from ordinary parsed SQL text — is
always `High`. `scan-db` and `scan-corpus-live` both accept
`--confidence high|medium` (default `high`) to control the least confident a
finding may be and still appear in the report; SARIF output maps a
below-`High` finding to level `note` and gives it a `/medium-confidence`-
suffixed rule ID, independently filterable from its `High` counterpart.

Nothing in the tool currently emits a below-`High` finding — the tier exists
ahead of the dynamic-SQL folder's planned symbolic-value inference, so that
work lands already gated: a finding derived from a value the folder could
only assume (a placeholder standing in for a variable it could prove had a
type but not a value — an uninitialized `DECLARE`, a proc parameter with no
known caller) will report at `Medium` and stay off by default, never mixed
into a `High` finding's numbers.

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
  (tables, columns, types, collations, indexes, scalar UDF/TVF metadata),
  `Lineage/` (view/TVF resolution, topo order, column provenance, TVF-fence
  and scalar-UDF inheritance maps), `Predicates/` + `Rules/` (extraction,
  precedence, verdicts, the TVF-fence/scalar-UDF scanners), `Reporting/`
  (JSON + SARIF), `Corpus/`.
* `src/SilentScan.Cli` — `scan-db` / `scan-corpus-live` commands.
* `src/SilentScan.Verify` — Docker-backed oracle: DDL deployment, `sys.columns`
  diffing, plan-XML confirmation for every stream (`verify-corpus`,
  `generate-type-matrix`).
* `src/SilentScan.Bench` — the benchmark harness.
* `tests/SilentScan.Tests` — xUnit tests and fixtures.

See `CLAUDE.md` for the full type-rule specification and project contract,
and `docs/local-dev.md` for further local-dev detail.
