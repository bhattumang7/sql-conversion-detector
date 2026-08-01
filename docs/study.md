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
did not appear in this specific 5-repo sample. These numbers are unchanged
after a substantial correctness pass across the tool (UPDATE/DELETE/MERGE
predicate coverage, IN-list and BETWEEN handling, inline/multi-statement TVF
resolution, `sysname`/`CREATE TYPE` alias resolution, and oracle-verified
fixes to the collation and literal-typing rules themselves) — re-running the
full pilot against the corrected tool reproduced the identical 76/79/3 split
for DNN Platform, which is reassuring rather than a coincidence: it means
none of those fixes happened to touch the specific predicates this pilot's
headline number rests on.

That correctness pass did surface one new, honestly-reported result: the
First Responder Kit now statically flags 22 `ScanForced` predicates (all new
UPDATE/DELETE/temp-table coverage; it had none before), but every one of them
is against a `#`-prefixed local temp table declared inside the very
stored-procedure body the Verify pass never deploys (CLAUDE.md: only
`ddlPaths` are deployed, never `procPaths` — the repo's own procedural logic
never executes). All 22 come back `ProbeFailed` ("Invalid object name
'#TraceStatus'", etc.) for that structural reason, not a classifier
disagreement, so none of them count toward the prevalence figure above. They
are real, plausible bugs by inspection (e.g. a temp table's `varchar` column
compared against a bare integer literal), but this pilot's methodology is not
built to engine-confirm anything living entirely inside a temp table's own
procedure, so they stay in the appendix below rather than the headline.

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

### Dynamic SQL: partial resolution, not a black box

`EXEC(@sql)`/`sp_executesql` hides its query text from any purely syntactic
scanner. Rather than lump every dynamic SQL call site into one "unanalyzable"
bucket, SilentScan proves each one constant where it honestly can:

- **Tier A** — the argument is already a literal, or a concatenation of bare
  literals.
- **Tier B** — `sp_executesql`'s own params-declaration argument gives exact
  parameter types (the classic ORM-generated shape: an `nvarchar` parameter
  bound against a `varchar`/`SQL_*` column).
- **Tier C** — the argument traces back through a straight-line chain of
  `DECLARE`/`SET`/`SELECT` assignments with no intervening branch, loop,
  `GOTO`, or function call, including nesting (dynamic SQL that itself
  contains dynamic SQL, resolved up to 5 levels deep).

A site proved constant this way is reparsed and run back through the same
catalog/lineage/predicate pipeline as static SQL, and any finding inside it
is attributed to its true source line — not the `EXEC` call site, which for
a multi-line folded string would make the location useless. Everything else
is still reported, with a specific machine-readable reason
(`diverges-across-if-branches`, `goto-or-label-in-scope`,
`non-literal-expression`, ...), never silently dropped.

Rerunning the same 5-repo corpus through this pass: of **1,041** dynamic SQL
call sites, **272 (26.1%)** were proven constant and fully analyzed like
static SQL; the remaining **769 (73.9%)** stayed honestly unanalyzable. The
unanalyzable reasons themselves are a finding: **46.8%** diverge across an
`IF` branch (the query text itself varies by condition — genuinely ambiguous,
not a gap in the folder), **25.6%** are disabled by a `GOTO`/label somewhere
in the same procedure (concentrated in Ola Hallengren's Maintenance Solution
and the First Responder Kit, both of which lean on `GOTO` for T-SQL error
handling), and **23.5%** depend on a non-literal expression such as a
function call. Reparsing the newly-analyzed 272 sites surfaced 32 additional
typed predicates (all in WideWorldImporters) — all `UNKNOWN` verdicts (an
operand type the reparse couldn't pin down), not new oracle-probeable
findings in this specific corpus. That's an honest negative result, not a
gap: this pass exists to make dynamic SQL visible to the same rigor as static
SQL, not to guarantee it surfaces new bugs in every corpus.

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
- **22 First Responder Kit findings are statically flagged but structurally
  unprobeable**, not refuted: every one resolves to a local `#` temp table
  declared inside a stored procedure body, and this pilot's Verify pass
  deploys only `ddlPaths` DDL, never a repo's own procedural logic
  (`procPaths`) — see the prevalence section above. Reported here rather than
  silently dropped or, worse, counted toward prevalence on faith.
- **`UNKNOWN` and unanalyzable rates**, recorded honestly rather than
  silently dropped: DNN Platform had 1,100 `UNKNOWN` typed comparisons (from
  cross-collation same-category comparisons this tool deliberately declines
  to resolve) and 110 dynamic-SQL call sites, of which 31 (28.2%) were
  proven constant and analyzed, 79 stayed unanalyzable; the First Responder
  Kit had 1,107 `UNKNOWN` (up from a much smaller count before the
  UPDATE/DELETE/MERGE predicate-coverage fix - those statements previously
  contributed no predicates, typed or UNKNOWN, at all) and 735 dynamic-SQL
  call sites (its monitoring scripts build most queries dynamically, and
  lean heavily on `GOTO` for error handling), of which only 99 (13.5%) were
  provably constant.
