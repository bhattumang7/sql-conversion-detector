# Audit remediation plan

Work items from the QA architecture audit, sequenced so that verification exists
before the risky changes, wrong answers are fixed before coverage grows, and
nothing lands without the evidence CLAUDE.md demands.

## Sequencing principle

Three rules drive the order below:

1. **Verification before mutation.** Anything that changes a verdict needs an
   oracle that can catch a regression in conversion *direction*. That harness is
   built first.
2. **Wrong answers before missing answers.** A finding that is wrong poisons the
   study; a finding that is missing only shrinks it. Every P0 in Phase 2 lands
   before Phase 4 widens the net.
3. **Nothing silently dropped.** The unanalyzable ledger is built in Phase 0 so
   every later phase registers its own gaps as it goes, instead of retrofitting
   honesty at the end.

## Definition of done (applies to every item, no exceptions)

- **Reproduce first.** Every item below states a defect inferred from reading the
  code. Before changing anything, write the test that demonstrates it and watch it
  fail. An item whose defect cannot be reproduced is struck from the plan, not
  fixed on faith.
- Minimal fixture that MUST fire, plus a near-miss fixture that MUST NOT fire.
- Fixture SQL derived from a real, citable, internet-sourced case — not invented.
  The source URL goes in a comment header in the fixture file.
- Oracle test in `SilentScan.Verify` for anything verdict-bearing, run against the
  local Docker SQL Server, compile-only.
- Integration test where the unit boundary cannot express the behaviour.
- `dotnet test` green; coverage at or above the 99% target for touched files.
- Sonar scan reporting 0 issues in every category.
- Conventional commit, authored as Umang Bhatt.

---

## Phase 0 — Foundations and safety net

No change to reported findings except additive counters. This phase exists so the
rest of the plan is verifiable.

### 0.1 Unanalyzable ledger across all passes

**Problem (audit B3, C5).** Pass 3 silently drops every comparison it cannot
handle: empty scope, unresolved operand, unsupported node kind, `IN` lists. There
is no counter, so the study cannot state its own coverage. `TypedPredicateExtractor`
also throws `NotImplementedException` on an unrecognised comparison operator,
which aborts an entire corpus scan.

**Work.**
- Introduce a `SkippedConstruct` record (pass, reason, source span, construct kind)
  and an accumulator threaded through Pass 1, 2, 3.
- Register into it from: catalog (unresolved `ALTER`/`CREATE INDEX` target,
  unresolvable type), lineage (view over unknown table, `SELECT *` that expands to
  zero columns, cyclic view), predicates (empty scope, unresolved operand,
  unsupported predicate node, unknown operator).
- Replace the `NotImplementedException` at `TypedPredicateExtractor.cs:101` with a
  ledger entry and a skip.
- Surface aggregate counts in `ScanReport` and the markdown/JSON output, alongside
  the existing dynamic-SQL bucket which is the model for this.

**Done when.** A scan of the mini-project fixture reports a non-zero, explainable
skip ledger, and a unit test asserts an unknown operator is counted rather than
thrown.

### 0.2 Type-pair oracle matrix

**Problem (audit C3).** `SqlType.IsWideningCompatibleWith` generalises a handful of
probed pairs into a blanket rule over the whole numeric x numeric and datetime x
datetime cross-product. Unprobed cells include pairs that are not mutually
comparable at all (`time` vs `date`).

**Work.**
- Generate the full scalar type-pair matrix as a data fixture; for each cell, deploy
  a two-column table and probe both comparison directions compile-only under
  SHOWPLAN_XML.
- Record per cell: does a column-side `CONVERT_IMPLICIT` appear, does a
  `GetRangeThroughConvert` dynamic seek appear, does compilation fail outright.
- Check the resulting matrix in as data with provenance per cell (probe date,
  server version, collation).
- Replace the two-family boolean with a lookup against that matrix; any cell not
  present resolves to UNKNOWN.
- Run the generation under both a SQL_* and a Windows collation database.

**Done when.** `VerdictClassifier` consults generated data, no hand-written family
heuristic remains, and a test asserts every cell the classifier relies on has a
recorded probe.

---

## Phase 1 — Collation plumbing

### 1.1 Connect the database default collation

**Problem (audit B5).** `DatabaseCatalog.DefaultCollation` has no writers and no
readers. `CorpusRepoEntry.DeclaredCollation` is loaded from the manifest and
dropped. Real DDL almost never carries per-column `COLLATE`, so essentially every
varchar-vs-nvarchar finding — the tool's marquee case — lands in UNKNOWN.

**Work.**
- Populate `DatabaseCatalog.DefaultCollation` from, in precedence order: an explicit
  `CREATE DATABASE ... COLLATE` in the scanned files, then the manifest's
  `declaredCollation`.
- Resolve a string column's effective collation as: explicit column `COLLATE`, else
  database default, else null (UNKNOWN preserved).
- Record on each finding which source supplied the collation, so the study can
  separate "confirmed from DDL" from "assumed from manifest".
- Validate the manifest collation value against the collation-name shape at load.

**Done when.** The same fixture scanned with and without a manifest collation
produces UNKNOWN in one case and SCAN_FORCED/RANGE_SEEK in the other, with the
provenance recorded, and an oracle test confirms both against a database created
with that exact collation.

---

## Phase 2 — Wrong-answer fixes

Every item here can currently produce an incorrect finding rather than UNKNOWN.

### 2.1 Unify column resolution and make qualified references strict

**Problem (audit A2).** `TypedPredicateExtractor.ResolveColumnOperand` falls back to
name-only matching when a qualifier fails to resolve, so `o.CustomerId` can bind to
a different table's `CustomerId` and inherit its type, index status and depth.
`ScalarExpressionResolver.ResolveColumnReference` handles the same case correctly,
so the two resolvers disagree.

**Work.**
- Extract one column resolver used by both Pass 2 and Pass 3.
- A qualified reference whose qualifier is not in scope resolves to unresolved —
  never a name-only fallback.
- Preserve the existing ambiguity rule (more than one match is unresolved).
- Route both unresolved outcomes into the Phase 0 ledger.

**Done when.** A fixture with two tables sharing a column name and a bad qualifier
produces no finding, and the near-miss with a correct qualifier produces one.

### 2.2 Scope chain for correlated subqueries

**Problem.** The scope stack exists but only the innermost frame is consulted, so a
correlated reference to an outer alias fails to resolve — and today falls into the
2.1 fallback.

**Work.** Walk the scope stack outward on lookup failure, innermost first. Add
depth tracking so an outer-scope resolution is still attributed correctly.

**Done when.** A correlated `EXISTS` fixture resolves its outer column reference to
the right base column, verified by oracle probe.

### 2.3 ALTER / CREATE OR ALTER procedure and function bodies

**Problem (audit A1).** `TypedPredicateExtractor` resets its variable table only on
`CreateProcedureStatement` and `CreateFunctionStatement`. `ALTER PROCEDURE` and
`CREATE OR ALTER PROCEDURE` are different node types, so their bodies are traversed
with the *previous* procedure's variable types still in scope, and their own
parameters never recorded. The "CREATE stub then ALTER with the real body" pattern
is ubiquitous; `DynamicSqlScanner` already handles it via
`ProcedureStatementBodyBase` and Pass 3 never got the same fix.

**Work.**
- Match on `ProcedureStatementBodyBase` and `TriggerStatementBody` in the typed
  extractor, mirroring the dynamic-SQL scanner.
- Reset and repopulate the variable scope per body.
- Audit the codebase for any other `CreateXStatement`-only match with the same
  latent bug.

**Done when.** A fixture with a `CREATE PROC` stub followed by `ALTER PROC` with a
differently-typed parameter classifies against the ALTER's parameter type, and a
regression test asserts no variable leaks across procedure boundaries.

### 2.4 Common table expressions

**Problem (audit A3).** CTEs are unhandled entirely, so a CTE named after a real
table resolves through the catalog to the *physical* table — wrong provenance,
wrong types, wrong index flag.

**Work.**
- Resolve `WithCtesAndXmlNamespaces` before the FROM clause; register each CTE as an
  inline relation reusing the `QueryDerivedTable` path (a CTE adds no view-layer
  depth).
- CTE names shadow catalog objects within their statement's scope.
- Honour explicit CTE column lists.
- Recursive CTEs: resolve the anchor branch, mark the recursive branch's
  contribution UNKNOWN rather than guessing, and register in the ledger.

**Done when.** A CTE shadowing a real table resolves to the CTE, the near-miss
without a CTE still resolves to the table, and a recursive CTE yields UNKNOWN.

### 2.5 Catalog correctness

**Problem (audit A5).** Four distinct defects in `CatalogBuilder`:
`ALTER TABLE ALTER COLUMN` is ignored so migration-heavy repos keep stale types —
which is precisely the varchar-to-nvarchar pattern the tool hunts; cross-file
ordering silently drops `ALTER`/`CREATE INDEX` whose target is not yet known;
proc-body containers are never recursed so temp tables and table variables inside
procedures are invisible, and `SELECT INTO #t` is unimplemented; database-qualified
names collapse across databases.

**Work.**
- Replace the hand-rolled statement switch with a `TSqlFragmentVisitor`, which kills
  the entire class of "container we forgot to enumerate" bugs.
- Handle `AlterTableAlterColumnStatement` (replace type and collation; unresolvable
  target type nulls the column so downstream goes UNKNOWN) and
  `AlterTableDropTableElementStatement`.
- Two-phase build: collect `CREATE TABLE` across all files, then apply `ALTER` and
  `CREATE INDEX`. Anything still unresolved goes to the ledger, not the floor.
- Implement `SELECT INTO` target inference for temp tables.
- Scope temp tables and table variables per procedure; colliding names across
  procedures must not clobber each other.
- Preserve the database qualifier in the catalog key; a qualifier naming a different
  database resolves to unresolvable rather than merging.
- Set NOT NULL for inline `PRIMARY KEY` columns (needed for the `sys.columns` diff).
- Record filtered-index predicates and index type, and stop counting a filtered or
  columnstore index as a plain seekable index for ranking.

**Done when.** A migration-script fixture (CREATE then ALTER COLUMN) yields the
post-ALTER type; an index-in-a-separate-file fixture marks the column indexed; two
procedures with same-named temp tables of different shapes each resolve correctly;
and the `sys.columns` diff over the mini project is clean.

---

## Phase 3 — Tier-1 false-positive elimination

### 3.1 Gate Tier-1 to real predicate contexts and fix direction

**Problem (audit A4).** Four distinct false-positive classes in
`NonSargablePredicateScanner`: it fires on comparisons anywhere in the tree
including SELECT-list `CASE` expressions; `HAVING SUM(Qty) > 5` is flagged as a
function-wrapped column though aggregates in HAVING are not a sargability concern;
`WHERE OrderTotal = Qty * Price` is flagged because arithmetic on *either* side
counts, replicating the exact direction error the type rules exist to avoid; and
`CAST(col AS date) = @d` is flagged though that is a documented optimizer exception
that still seeks.

**Work.**
- Track predicate context explicitly; fire only inside WHERE, JOIN ON, and the
  filter portion of HAVING — never a SELECT list, never a CASE result.
- Exclude aggregate functions from the function-wrapped-column rule.
- Restrict the arithmetic rule to the side bearing the candidate probe column,
  determined by which side resolves to a base column.
- Special-case datetime-to-date `CAST`/`CONVERT` as a conditional verdict, not a
  flat non-sargable, and verify against the oracle.

**Done when.** Each of the four cases has a MUST-NOT-fire fixture, each keeps a
sibling MUST-fire fixture, and the mini-project scan's Tier-1 count drops with every
removed finding individually justified.

---

## Phase 4 — Coverage expansion

Only after Phase 2 and 3, so the widened net does not amplify wrong answers.

### 4.1 UPDATE, DELETE and MERGE predicates

**Problem (audit B1).** The extractor pushes scope only on `QuerySpecification`, so
a `WHERE` on an `UPDATE` or `DELETE` hits the empty-scope early return and vanishes.
In OLTP procedures this is where a large share of index-killing predicates live.
This is the single biggest coverage gap in the tool.

**Work.** Push a FROM scope for `UpdateStatement`, `DeleteStatement` and
`MergeStatement`, including their `FROM` extensions and, for MERGE, the target,
source and `ON` clause.

**Done when.** Real-world-sourced UPDATE and DELETE fixtures produce findings
confirmed by oracle probe, and the study's denominator is recomputed.

### 4.2 Inline table-valued function references

**Problem (audit B2).** `FromScopeResolver` handles only `NamedTableReference` and
`QueryDerivedTable`. A TVF call is a `SchemaObjectFunctionTableReference` and falls
to the empty-relation default, so the iTVF lineage machinery can never fire from a
FROM clause and the "mismatch inherited through TVF layers" story cannot materialise.

**Work.** Resolve `SchemaObjectFunctionTableReference` against the lineage catalog's
resolved TVFs, treating an inline TVF as a view layer for depth purposes and a
multi-statement TVF as its declared RETURNS shape.

**Done when.** A predicate over a column from `FROM dbo.fn_X(@p)` resolves to the
base column with depth at least 1.

### 4.3 IN lists and remaining predicate forms

**Problem (audit B3).** `InPredicate` is unhandled, so `col IN (1, '2', N'3')` —
the mixed-literal case the spec itself calls out — appears in neither findings nor
any bucket.

**Work.** Handle `InPredicate` with literal lists (resolve the list's effective type
by precedence across elements, classify against the column) and with subqueries
(resolve the subquery's single output column through lineage). Ensure BETWEEN
covers both bounds rather than standing in with the lower bound alone.

**Done when.** A mixed-type IN list fixture fires with the correct converted side
confirmed by oracle, and a homogeneous IN list does not.

### 4.4 Per-batch parse recovery

**Problem (audit B4).** One parse error discards an entire file from catalog,
lineage and predicates — and losing that file's tables poisons every other file's
views over them, turning one syntax quirk into a cascade.

**Work.** Retain batches that parsed cleanly by mapping ScriptDOM's error spans to
batches; drop only the affected batches. Report per-file batch-level health. Retry
once with `QUOTED_IDENTIFIER OFF` on failure and keep the better parse. Add an
encoding fallback for non-UTF-8 files.

**Done when.** A file with one bad batch still contributes its other batches'
tables, and parse health reports batch granularity.

---

## Phase 5 — Oracle and probe fidelity

### 5.1 Verify RANGE_SEEK, not just conversion

**Problem (audit C1).** `ConvertImplicitDetector` proves column-side conversion,
which validates direction — but SCAN_FORCED and RANGE_SEEK both produce a
column-side convert, so the collation claim (the most citable part of the study) is
only half-verified.

**Work.** Extend the verifier to assert plan shape for collation-dependent verdicts:
RANGE_SEEK findings must show a dynamic seek (`GetRangeThroughConvert`) under a
Windows-collation database; SCAN_FORCED must not under a SQL_* collation. Run the
collation pairs under both database collations.

**Done when.** Every RANGE_SEEK and SCAN_FORCED rule has an oracle test asserting
plan shape, not only conversion presence.

### 5.2 Probe fidelity for literal operands

**Problem (audit C2).** `CorpusFindingProbeBuilder` substitutes a typed `DECLARE @p`
for literal operands. The optimizer constant-folds literals in ways it cannot for
variables, so a probe can show a convert the original predicate would not.

**Work.** For literal-operand findings, probe with a reconstructed literal of the
recorded type (still self-authored, never corpus code). Where reconstruction is not
possible, record the substitution as a per-finding caveat rather than treating the
probe as equivalent.

**Done when.** Literal-operand findings probe with literals, and any remaining
substitution is visible in the finding record.

### 5.3 Literal typing corrections

**Problem (audit C4).** Integer literals beyond int range are typed `Int` rather
than promoting; scientific-notation literals fall to null; empty string gets
`Length: 0`.

**Work.** Promote out-of-range integer literals per T-SQL rules, handle
`RealLiteral`, and correct the zero-length case. Confirm each against the oracle
before encoding.

---

## Phase 6 — Design hygiene

### 6.1 Move corpus-specific behaviour out of Core

`CorpusTemplatePreprocessor` hardcodes repo-specific token substitutions in Core;
these belong in `manifest.json` as a per-entry substitution map so adding a repo is
not a code change. `SqlFileDiscovery` hardcodes `*.sql`, so the bare `scan` command
finds nothing in repos shipping other extensions — accept an extension list.

### 6.2 Alias and user-defined types

`SqlTypeReferenceResolver` returns null for every `UserDataTypeReference`, which
includes `sysname` (equivalent to `nvarchar(128)`) — pervasive in the admin-script
repos this study targets. Special-case `sysname`, catalog `CREATE TYPE ... FROM`
aliases and resolve through them; anything else stays UNKNOWN.

---

## Phase 7 — Re-run the pilot gate

The audit invalidates the existing pilot: verdicts change under Phase 1, findings
are removed under Phase 3, and findings are added under Phase 4.

- Re-run the 5-repo pilot and hand-verify 100% of findings.
- Recompute precision on the oracle-confirmed set against the >95% target.
- Publish the skip ledger alongside the prevalence numbers so coverage is bounded
  honestly.
- Rewrite `docs/study.md` numbers; the current ones do not survive this plan.

---

## Ordering summary

| Phase | Theme | Gate before proceeding |
|---|---|---|
| 0 | Ledger, type-pair oracle matrix | Matrix generated and checked in |
| 1 | Collation plumbing | Oracle confirms both collation families |
| 2 | Wrong-answer fixes | No known path yields a wrong verdict |
| 3 | Tier-1 false positives | Every removed finding individually justified |
| 4 | Coverage expansion | New surfaces oracle-confirmed |
| 5 | Oracle and probe fidelity | Plan-shape verification in place |
| 6 | Design hygiene | — |
| 7 | Pilot re-run | Precision target met on re-verified set |

## Notes on effort shape

Phase 2.5 and Phase 4.1 are the largest single items; both are structural rather
than fiddly. Phase 0.2 is mostly machine time once the generator exists. Phase 3 is
small in code and large in fixture research, because each MUST-NOT-fire case needs
a real sourced example under the no-invented-corpus rule.
