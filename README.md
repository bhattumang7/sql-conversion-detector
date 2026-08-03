# SilentScan

Static analyzer for index-killing implicit conversions in T-SQL — including
ones inherited through layers of views and inline table-valued functions —
plus a corpus study pipeline that quantifies their prevalence and cost across
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

## Scan SQL for implicit-conversion findings

```
dotnet run --project src/SilentScan.Cli -- scan <path-to-.sql-or-folder> [--format text|markdown|json|sarif] [--collation <collation>] [--output <file>]
```

Parses every `.sql` file under a path (or a single file), reports ScriptDOM
parse health, and reports sargability findings — syntactic (Tier-1) and
typed-verdict (`SeekPreserved | RangeSeek | ScanForced | Unknown`). Pass
`--collation` with the database's default collation to resolve the
varchar-vs-nvarchar rule for columns without their own `COLLATE` clause;
omit it to instead get a collation-sensitivity report scored under both a
representative `SQL_*` and Windows collation. `--extensions` lets you scan
non-`.sql` DDL file extensions (e.g. DNN Platform's `.SqlDataProvider`).
Output defaults to `--format text`: a readable report that groups findings by
what is wrong with them, explains each group once, and gives every finding its
location, base column, indexed flag and the view layer that introduced the
mismatch. `--format markdown` is the same report as a shareable document,
`--format json` is the complete versioned findings schema, and `--format sarif`
emits SARIF for CI gating. `--output <file>` writes the report to a file
instead of standard output.

## Corpus study pipeline

`corpus/manifest.json` pins the repo set (URL, commit SHA, license, DDL/proc
paths, declared collation). Clone the pinned repos into `corpus/_clones/`
first, then:

```
dotnet run --project src/SilentScan.Cli -- scan-corpus [--manifest corpus/manifest.json] [--clones-root corpus/_clones] [--format text|markdown|json] [--output <file>]
```

Scans every manifest repo and reports per-repo findings, gated on each
repo's ScriptDOM parse-health passing the dialect-sniffing bar. The readable
formats lead with a one-row-per-repo rollup table — findings, parse rate,
dialect-sniffing result, whether a collation was pinned — followed by each
repo's full report. SARIF is not offered here: one log cannot honestly
describe five separate trees.

```
dotnet run --project src/SilentScan.Verify -- verify-corpus [--manifest corpus/manifest.json] [--clones-root corpus/_clones] [--repo <name>]
```

Deploys each repo's DDL to a fresh disposable database, diffs inferred
view/TVF column types and collations against `sys.columns`, and for each
`ScanForced` finding submits a compile-only `SET SHOWPLAN_XML ON` probe to
confirm `CONVERT_IMPLICIT` lands on the column side of the predicate.
Corpus DML and stored procedures are never executed — only self-authored
probes run against the disposable database.

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
  (tables, columns, types, collations, indexes), `Lineage/` (view/TVF
  resolution, topo order, column provenance), `Predicates/` + `Rules/`
  (extraction, precedence, verdicts), `Reporting/` (JSON + SARIF), `Corpus/`.
* `src/SilentScan.Cli` — `scan` / `scan-corpus` commands.
* `src/SilentScan.Verify` — Docker-backed oracle: DDL deployment, `sys.columns`
  diffing, plan-XML `CONVERT_IMPLICIT` confirmation (`verify-corpus`,
  `generate-type-matrix`).
* `src/SilentScan.Bench` — the benchmark harness.
* `tests/SilentScan.Tests` — xUnit tests and fixtures.

See `CLAUDE.md` for the full type-rule specification and project contract,
and `docs/local-dev.md` for further local-dev detail.
