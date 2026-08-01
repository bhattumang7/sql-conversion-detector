CLAUDE.md — SilentScan (working title)

Static analyser + corpus study: find index-killing implicit conversions in T-SQL,
including ones inherited through layers of views/TVFs, and quantify their prevalence
and cost across public open-source codebases.

What this project is

Two deliverables, one codebase:

1. The tool: a CLI (silentscan) that ingests a folder of .sql files
(DDL + views + procs), builds a column lineage graph with type propagation,
and reports column-side implicit conversions and non-sargable predicates —
ranked by whether the column is indexed and how many layers deep the type
mismatch originates.
2. The study: run the tool over a curated corpus of well-known open-source
SQL Server projects, verify findings against a real SQL Server in Docker,
benchmark the cost, and publish prevalence numbers.

Fame goal, not commercial. Precision beats recall everywhere: one false positive
in the published study is worse than ten missed true positives.

Hard scope decisions (do not revisit without asking)

* SQL Server / T-SQL only. Parser is Microsoft.SqlServer.TransactSql.ScriptDom
(NuGet, first-party, free). No MySQL/Postgres/Oracle in v1. No ANTLR.
* EF Core / ORM analysis is explicitly OUT of v1. SQL text only.
* No execution of corpus code except inside the disposable Docker SQL Server
used for verification/benchmarks.
* Target framework: .NET 10—language: C#.

Architecture (4 passes)

src/
  SilentScan.Core/
    Parsing/        # ScriptDOM wrapper, batch splitting, error tolerance
    Catalog/        # Pass 1: tables, columns, types, collations, indexes, PK/UQ
    Lineage/        # Pass 2: view/iTVF resolution, topo order, column provenance
    Predicates/     # Pass 3: predicate extraction + type comparison
    Rules/          # Precedence matrix, collation rules, verdict classification
    Reporting/      # Pass 4: ranked findings, JSON + SARIF + markdown output
  SilentScan.Cli/
  SilentScan.Verify/   # Docker SQL Server: deploy DDL, diff sys.columns,
                       # capture plan XML, grep CONVERT_IMPLICIT
  SilentScan.Bench/    # cost harness (see Benchmark protocol)
tests/
  SilentScan.Tests/    # xUnit; every rule gets a minimal repro .sql fixture
corpus/                # cloned repos (gitignored), manifest.json checked in
docs/

* Pass 1 (Catalog): CREATE TABLE, ALTER TABLE, CREATE INDEX, computed
columns, temp tables (#t via SELECT INTO and CREATE TABLE #t), table
variables, database default collation from any CREATE DATABASE/manifest hint.
* Pass 2 (Lineage): resolve every view output column to
BaseColumn(table, column, type, collation) | Expression(inferredType) |
Cast(explicitType) | Unknown(reason). Views in topological order of
dependency; cycles → Unknown. SELECT * expands against catalog. UNION/UNION
ALL output type = highest precedence across branches (record ALL branch types —
the mixed-branch case is itself a finding). Inline TVFs = views. Multi-statement
TVFs read declared RETURNS table types.
* Pass 3 (Predicates): comparison predicates in WHERE/ON/HAVING of procs, views,
functions, ad-hoc statements. For each colRef <op> other, resolve colRef
through lineage to base type + collation; determine other side’s type (literal
typing rules, parameter/variable declarations, other column via lineage).
* Pass 4 (Verdict + rank), per finding:
  * verdict: SEEK_PRESERVED | RANGE_SEEK (dynamic seek, partial penalty) |
SCAN_FORCED | NOT_SARGABLE_FUNCTION | OPERAND_CLASH | UNKNOWN
  * indexed: is the base column a key column of any index / PK / UQ?
  * depth: 0 = direct table predicate; N = layers of views/TVFs between the
predicate and the base column
  * origin: file/line of predicate AND file/line of the layer that introduced
the mismatch (e.g., the CAST inside vw_X)
  * Rank: SCAN_FORCED + indexed + depth>=1 first.

The type rules (the heart of the tool — get these exactly right)

* Only column-side conversion loses the seek. T-SQL data type precedence
converts the LOWER-precedence side. varchar column vs nvarchar
value/param → the COLUMN converts → seek lost (subject to collation, below).
nvarchar column vs varchar value → the VALUE converts → harmless
(SEEK_PRESERVED). Direction errors are the #1 way this study dies in public.
* Precedence matrix: encode from the official T-SQL data type precedence
list. Ground-truth seek/scan outcomes per type-pair: Kehayias’ implicit
conversion matrix (sqlskills.com) — cite it, verify it against our Docker
oracle for the pairs we actually report, don’t blindly trust either source.
* Collation is a first-class input. varchar column vs nvarchar value:
  * SQL_* collations (e.g. SQL_Latin1_General_CP1_CI_AS): SCAN_FORCED.
  * Windows collations (e.g. Latin1_General_CI_AS): engine can build a dynamic
range seek (GetRangeThroughConvert) → classify RANGE_SEEK, not SCAN_FORCED,
and benchmark it separately (it is cheaper than a scan, dearer than a seek).
  * If column collation unknown (no explicit COLLATE, no db default found):
verdict UNKNOWN unless the manifest pins a collation. Never guess silently.
* Non-sargable functions (Tier-1 syntactic, no type info needed):
function-wrapped column (YEAR(col)=, UPPER(col)=, ISNULL(col,x)=,
CONVERT/CAST(col,...)=), column arithmetic (col+1=), leading-wildcard
LIKE '%...', LIKE @p marked conditional.
* Literal typing: N'x' = nvarchar, 'x' = varchar, integer literal = int,
1.5 = numeric(p,s), date literals stay strings (varchar) until compared.
* Known hard cases — implement as explicit rules with test fixtures, or emit
UNKNOWN: CASE/COALESCE/NULLIF result typing (precedence of branches), IN lists
(mixed literal types), BETWEEN, computed columns (persisted + indexed can
still seek), sql_variant → OPERAND-ish special, date/time family vs string.

Dynamic SQL policy

EXEC(@sql) / sp_executesql with concatenated strings: do NOT attempt full
analysis. If the argument is a single string literal or trivially constant-folded
concatenation of literals, parse it. Otherwise count the statement in an
unanalyzable bucket that is REPORTED in the study (“X% of procs contain
dynamic SQL we could not analyze”) — never silently counted as clean.

Cardinality estimator & server settings — the policy

Classification (seek vs scan shape) is driven by type precedence + collation

* predicate form. The CE version (legacy 70 vs new 120+) changes ROW ESTIMATES
and COSTS, not the sargability of a predicate — but misestimates can flip the
optimizer’s seek/scan CHOICE in borderline cases, and conversions themselves
degrade estimates (that’s part of the tax). So:
* The static verdict never depends on CE. It states what the predicate makes
possible for the engine.
* The oracle test is plan-XML based, not shape based: a finding is confirmed
iff the actual plan contains CONVERT_IMPLICIT applied to the COLUMN side of
the predicate (search ScalarOperator/Convert with Implicit=“true” over a
ColumnReference). Whether the plan happens to seek or scan on tiny data is
irrelevant and must not be used as the pass/fail signal.
* Benchmarks pin the environment (see below) and run the cost comparison
under BOTH CE versions (LEGACY_CARDINALITY_ESTIMATION ON/OFF) so the paper
can say “the tax exists under both estimators; magnitudes were X and Y.”
* Settings that actually matter and are controlled: database compatibility level
(pin 160), database collation (test both one SQL_* and one Windows collation),
LEGACY_CARDINALITY_ESTIMATION (both), MAXDOP 1 for reproducibility,
SET STATISTICS IO/TIME capture, warm cache (report warm numbers; note cold).
Settings we note as non-factors for classification: parameter sniffing,
optimize-for-adhoc, memory grants (report if they distort a specific bench).

Benchmark protocol (SilentScan.Bench)

Docker mcr.microsoft.com/mssql/server:2022-latest. One synthetic table per
type-pair under test; row counts 10K / 1M / 10M; identical query with matching
vs mismatching parameter type; capture logical reads, CPU ms, elapsed ms,
estimated vs actual rows, plan XML. Each cell = median of 5 warm runs. Output a
CSV the writeup can chart directly.

Verification workflow (SilentScan.Verify)

For each corpus repo: deploy its DDL to a fresh database → diff our inferred
view column types/collations against sys.columns for every view (this is the
free ground-truth oracle for the lineage engine; ANY mismatch is a P0 bug) →
for each SCAN_FORCED finding, submit a SELF-AUTHORED probe SELECT (never the
repo’s procs) under SET SHOWPLAN_XML ON — compile-only, nothing executes, no
data needed (tables stay empty; CONVERT_IMPLICIT is a compile-time artifact
visible in the estimated plan) — and confirm CONVERT_IMPLICIT-on-column in the
returned plan XML. Corpus DML/procs are NEVER executed anywhere. Study reports
only oracle-confirmed findings; static-only findings go in an appendix.

Corpus rules

* corpus/manifest.json (checked in): repo URL, commit SHA pinned, license,
which paths contain DDL vs procs, declared/assumed collation.
* Only repos whose SQL is plausibly SQL Server: heuristics = GO batch
separators, bracket quoting, dbo., ScriptDOM parse success rate ≥ 90% of
files. Skip files that fail dialect sniffing — a MySQL file parsed as T-SQL
is noise, not signal.
* Ethics: aggregate stats public. No maintainer-outreach requirement before
naming a project in the writeup — do not file GitHub issues/PRs on scanned
repos as part of this workflow. Never name-and-shame in tone.

Precision discipline

* Every rule ships with: a minimal fixture that MUST fire, a near-miss fixture
that MUST NOT fire, and (if verdict-bearing) an oracle test in Verify.
* Pilot gate: before scanning the full corpus, run on 5 repos and hand-verify
100% of findings. Published precision target: >95% on oracle-confirmed set.
* When inference is uncertain → UNKNOWN, never a guess. UNKNOWN counts are
reported honestly in the study.

Conventions

* xUnit; fixtures live in tests/fixtures/*.sql, named RULEID_fires.sql /
RULEID_clean.sql.
* Findings schema is versioned JSON; SARIF export so the tool doubles as a CI
gate later (GitHub Action = phase 2 distribution).
* Conventional commits. No network calls in Core—deterministic output ordering.
* Run everything on Ubuntu; assume Docker available; dotnet test must pass
before any commit that touches Rules/ or Lineage/.

Git

  * Never mention Claude or Gemini or any other company or model as co-author in this.
  * Always commit using Umang Bhatt (bhatt.umang7@gmail.com).
  * If you are resolving 10 items from a list, the commit message should never say “resolve item #1” because those details have no relevance when we are going back.

Hallucination

  * We want to do things right in the first go. Do not write any placeholders or dummy values that would have to be cleared later. All that you can do is not implement exceptions.

Sonar

  * Sonar is available here, and we scan using it before each commit. All issues are resolved before commit.  I have brought in a sonar scanning script from another project - tweak it as needed.
  * Compile as well as Sonar should report 0 issues in all categories.
  * Run Sonar and get to 0 issues before every commit.

Local database

* We have a local SQL Server image in Docker. Use that.

Code coverage

* We always aim for 99% code coverage.

Inventing our own corpus

* Do not invent our own corpus; look for issues from the internet and include those as tests for us to detect.

Test fixtures

* The test fixtures need to be repeatable. Make sure to clean up correctly at the end to avoid flaky tests.

Tests

* A balance between unit and integration tests needs to be maintained.
