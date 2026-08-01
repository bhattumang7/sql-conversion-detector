# SilentScan: Index-Killing Implicit Conversions in T-SQL

A static analyzer + corpus study for the "silent" implicit-conversion tax that
defeats indexes in SQL Server, including cases inherited through layers of
views and table-valued functions.

## The two numbers

**Prevalence:** 1 of 5 scanned open-source SQL Server projects (20%) contains
at least one engine-confirmed index-killing implicit conversion. In that
project, 76 of 79 statically-flagged predicates were confirmed against a real
SQL Server plan (0 refuted; 3 could not be probed due to a schema-deployment
ordering issue, not a verdict error) — **100% precision on the
oracle-confirmed set**. None of the confirmed findings were inherited through
a view or TVF layer in this pilot (all were direct base-table predicates,
depth 0); the "inherited through views" phenomenon the tool is built to catch
did not appear in this specific 5-repo sample.

**Cost:** at 10M rows, a mismatched-collation `varchar` vs `nvarchar`
comparison costs **33,572 logical reads vs 3** for the matched case — an
**~11,190× increase** — and takes 871ms of CPU vs effectively 0ms. This holds
under both the legacy and current cardinality estimators. A same-collation
mismatch (Windows collation, which supports a dynamic range seek) and a
same-family numeric widening (`int` vs `bigint`) both showed **no measurable
cost difference** at any row count tested — the engine truly eliminates the
cost in those cases, not just reduces it.

## Methodology

### The tool

`silentscan` parses `.sql` files with Microsoft's own T-SQL parser
(ScriptDOM), builds a table/view/TVF catalog and a column lineage graph with
type propagation, then classifies every column-vs-other comparison predicate
using the T-SQL data type precedence rules:

- **Direction matters.** Only the lower-precedence side of a comparison
  converts. A `varchar` column compared to an `nvarchar` value loses its
  index (the column converts); the reverse does not. Getting this backwards
  is the most common way this kind of analysis is wrong in public, so every
  verdict is derived from the official precedence order, not a guess.
- **Collation is a first-class input** for string-family conversions: `SQL_*`
  collations force a full scan; Windows collations let the engine build a
  dynamic range seek instead, which is real but visibly cheaper.
- **Same-family widening is free.** Empirically verified against a live SQL
  Server: `int` vs `bigint`, `bit` vs `int`, `date` vs `datetime`, and other
  same-family pairs never produce `CONVERT_IMPLICIT` in the plan, regardless
  of which side's precedence rank is lower — an early version of this tool
  flagged these as false positives before this was verified against the
  oracle and corrected.
- **Never guesses.** Unresolvable collation, ambiguous types, or unsupported
  constructs are reported as `UNKNOWN`, not classified either way.

Static classification never depends on the cardinality estimator or server
settings — it describes what a predicate's *shape* makes possible, not
whether a particular tiny test table happens to seek or scan.

### Engine confirmation (the oracle)

A static verdict is a hypothesis. It is confirmed by deploying the scanned
repo's own DDL to a disposable SQL Server (compatibility level 160), then
compiling a minimal, self-authored probe statement under
`SET SHOWPLAN_XML ON` — compile-only, nothing executes, no corpus code ever
runs — and checking the estimated plan for `CONVERT_IMPLICIT` applied to the
finding's own column. Only findings with this signature are counted in the
oracle-confirmed set; anything else (not probeable, or the deployed schema
didn't match) is reported honestly rather than assumed.

### Corpus

Five open-source projects with real DDL and stored procedures/views checked
into the repository (`corpus/manifest.json`, commit SHAs pinned):
WideWorldImporters, DNN Platform, Brent Ozar's SQL Server First Responder
Kit, Ola Hallengren's SQL Server Maintenance Solution, and mojoPortal. All
five parsed at 100% ScriptDOM success. The corpus is intentionally small for
this first pass — broadening it is future work, not part of this writeup.

### Benchmark

Synthetic tables at 10K / 1M / 10M rows, matched vs. mismatched predicate,
both `LEGACY_CARDINALITY_ESTIMATION` settings, `MAXDOP 1`, median of 5 warm
runs, logical reads / CPU / elapsed captured via `SET STATISTICS IO/TIME`.
Full results: [`bench-results.csv`](bench-results.csv).

## Honest caveats

- **n = 5 repos.** The 20% prevalence figure is a pilot-scale number, not a
  broad survey claim. All confirmed findings came from a single project
  (DNN Platform); the other four had zero.
- **No confirmed view-inherited findings in this sample.** The tool's lineage
  engine and depth attribution are built, tested, and oracle-verified on
  synthetic fixtures (including a 5-deep view chain), but this pilot corpus
  did not happen to surface a real confirmed finding at depth ≥ 1.
- **3 DNN findings are unconfirmed, not refuted**, due to a DDL
  cross-file foreign-key deployment-ordering limitation in the verifier, not
  a classifier disagreement.
- **`UNKNOWN` and unanalyzable rates**, recorded honestly rather than
  silently dropped: DNN Platform had 1,142 `UNKNOWN` typed comparisons (from
  cross-collation same-category comparisons this tool deliberately declines
  to resolve) and 110 unanalyzable dynamic-SQL statements; the First
  Responder Kit had 315 `UNKNOWN` and 735 dynamic-SQL statements (its
  monitoring scripts build most queries dynamically).
