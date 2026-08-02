# Local development

## SQL Server (verification oracle)

```
cp .env.example .env      # override SILENTSCAN_SA_PASSWORD if you want
docker compose up -d
```

Connects on `localhost,14330`, user `sa`. Used by `SilentScan.Verify` (lineage
oracle against `sys.columns`, plan-XML `CONVERT_IMPLICIT` confirmation) and
`SilentScan.Bench`. Compat level is pinned to 160 per-database by the tooling,
not at the server level — each spike/bench database sets it explicitly after
`CREATE DATABASE`.

## Build & test

```
dotnet build
dotnet test
```

`Directory.Build.props` treats warnings as errors and enables recommended
analyzers solution-wide; a red build is a real defect, not noise to suppress.

The Docker SQL Server above is a hard requirement for `dotnet test` overall,
not just for `Integration/`: verdict-bearing tests across `Predicates/` also
deploy their fixture DDL and confirm against a real SHOWPLAN_XML plan
(`[Trait("Category", "Oracle")]` / `OracleTestFixture`), not just that the
static pipeline agrees with itself. No mock/skip path.

## Benchmark harness

```
dotnet run --project src/SilentScan.Bench -- run \
  --rows 10000 1000000 10000000 \
  --output silentscan-bench-results.csv
```

Runs the full CLAUDE.md Benchmark protocol matrix (type pairs x row counts x
legacy/new cardinality estimator x matched/mismatched param) against a fresh
disposable database and writes the cost table CSV. The default row counts
match the spec (10K/1M/10M); the automated test suite exercises the same
code path at a much smaller row count to keep `dotnet test` fast — run the
CLI directly for the full-scale sweep.

## Sonar

```
pwsh ./sonar-scan.ps1              # build + test + coverage + upload
./sonar-check-issues.sh            # print open issues + quality gate status
```

The SonarQube MCP server is disabled in this session (it pages full issue
objects and burns context). `sonar-check-issues.sh` hits the REST API
directly with `curl`/`jq` and prints a compact table instead — use that to
check gate status before every commit, per CLAUDE.md.
