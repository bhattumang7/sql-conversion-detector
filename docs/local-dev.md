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
scripts/dotnet-safe.sh build
scripts/dotnet-safe.sh test
scripts/dotnet-safe.sh test --filter "FullyQualifiedName~DynamicSql"
```

Always go through `scripts/dotnet-safe.sh`, never a bare `dotnet build`/
`dotnet test` piped into `tail`/`head`/`grep`/etc. Both commands spawn
detached MSBuild "node reuse" worker processes that inherit the invoking
shell's stdout/stderr and deliberately outlive the command so a later build
can reuse them. Piping the output into another process makes the shell wait
for EVERY holder of the pipe's write end to close it - the reused workers
never do, so the pipeline hangs indefinitely even though the real command
already finished. Reproduced directly in this repo (2026-08-03): a `dotnet
test | tail -60` sat for 20+ minutes after `dotnet test` itself had already
exited, and repeated kill-instead-of-exit across sessions left thousands of
orphaned `/tmp/MSBuildTemp*` directories. `scripts/dotnet-safe.sh` sets
`MSBUILDDISABLENODEREUSE=1` (so there is no worker process left to hold a
pipe open in the first place), always redirects to a real log file instead
of a pipe, wraps the run in a hard `timeout` as a backstop, and shuts down
any build server on exit regardless of outcome. It prints the last 60 lines
and the full log path; read the log file directly for more.

`Directory.Build.props` treats warnings as errors and enables recommended
analyzers solution-wide; a red build is a real defect, not noise to suppress.
It also disables Roslyn's shared compiler server (`UseSharedCompilation`) -
reproduced directly that two `dotnet build` invocations against this
checkout racing the same server process can crash it outright ("Internal CLR
error 0x80131506"). That doesn't make concurrent builds SAFE by itself
(they can still race writing the same obj/bin output, MSB3026/MSB3030) -
never run two `dotnet build`/`dotnet test` invocations against this checkout
at the same time; `sonar-scan.ps1` guards itself against this with its own
build lock (`.sonar-scan.lock`), but a manually-run `dotnet build` or
`dotnet test` you launch yourself isn't covered by it.

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
pwsh ./sonar-scan.ps1              # build + test + coverage + upload + wait + print result
pwsh ./sonar-scan.ps1 -Verbose     # same, with full scan/build/test output as it runs
```

The SonarQube MCP server is disabled in this session (it pages full issue
objects and burns context). `sonar-scan.ps1` hits the REST API directly
instead, in the same script that ran the scan — quiet by default (a one-line
"Quality gate: OK" when clean), full file:line/severity/rule/message detail
for every issue/hotspot when not. Use it to check gate status before every
commit, per CLAUDE.md.
