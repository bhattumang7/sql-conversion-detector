# Coverage remediation plan

Second remediation pass. `docs/audit-remediation-plan.md` fixed defects found by
reading the code we had written. This one fixes the reason those defects kept
being found one at a time.

## The root cause this plan exists to fix

Every coverage item in the first plan was discovered bottom-up — by reading our
own visitors and noticing a ScriptDOM node type they failed to match. Nothing has
ever enumerated the T-SQL construct space top-down and asked which constructs we
handle. There is no coverage matrix in the repo.

Two consequences, both visible in the current tree:

1. **Fixes land on the instance, not the class.** `TypedPredicateExtractor.cs:108`
   carries a twelve-line comment explaining ScriptDOM's double-dispatch trap.
   Below it, procedures get three overloads and functions get three overloads;
   triggers get two (`CreateOrAlterTriggerStatement` is missing). `CatalogBuilder`
   repeats the same comment and the same omission. `ViewDefinitionExtractor` — the
   third pass — never got the lesson at all, so `CREATE OR ALTER VIEW` is invisible
   to the lineage engine the study's headline claim rests on.
2. **Corpus-green stands in for correct.** The pinned five repos are
   stylistically narrow: 195 `CREATE VIEW`, one `ALTER VIEW`, zero `CREATE OR
   ALTER` of anything, zero CLR, one trigger file. The tool passes the corpus while
   being broken for the idiom most modern SSDT codebases use.

So the ordering principle for this plan is: **build the instrument before doing
the work.** The coverage matrix and default-ledgering land first, so that every
later item is a matrix cell flipping from GAP to HANDLED and the delta is
auditable rather than remembered.

## Sequencing principles

Carried forward from the first plan:

1. **Verification before mutation.**
2. **Wrong answers before missing answers.** A finding that is wrong poisons the
   study; a finding that is missing only shrinks it.
3. **Nothing silently dropped.**

Added here:

4. **Fix the class, not the instance.** Any item that fixes a node-type omission
   must be accompanied by a sweep proving no sibling omission remains, and where
   possible by a mechanical check that fails the build on the next one.
5. **The corpus does not certify coverage.** Coverage is certified by the matrix
   and its fixtures. The corpus certifies precision on real code, nothing more.

## Definition of done (every item, no exceptions)

Unchanged from `docs/audit-remediation-plan.md`, restated because it governs here
too:

- **Reproduce first.** Every item below states a defect inferred from reading the
  code. Write the failing test before changing anything. An item whose defect
  cannot be reproduced is struck from the plan, not fixed on faith.
- Minimal fixture that MUST fire, plus a near-miss that MUST NOT.
- Fixture SQL derived from a real, citable, internet-sourced case — URL in a
  comment header in the fixture file.
- Oracle test in `SilentScan.Verify` for anything verdict-bearing, compile-only
  against the local Docker SQL Server.
- `dotnet test` green, coverage at or above 99% for touched files.
- Sonar scan at 0 issues in every category.
- Conventional commit, authored as Umang Bhatt. No phase numbers in the message.

---

## Phase 0 — Instrumentation and hardening

No change to any reported verdict. This phase makes the rest of the plan
measurable.

### 0.1 Construct coverage matrix

**Problem.** No artifact states which T-SQL constructs the tool analyzes. Coverage
claims in `docs/study.md` are therefore unfalsifiable, and gaps are found by
accident.

**Work.**
- Enumerate every ScriptDOM statement type that can *define* a typed column, *carry*
  a predicate, or *introduce* a name into a query scope. Not all ~200 statement
  types — the closure of those three questions, which is on the order of 40.
- For each, record: pass(es) responsible, status (`HANDLED` / `LEDGERED` /
  `GAP`), the fixture that proves it, and a one-line rationale for anything
  deliberately excluded.
- Check it in as data (`docs/construct-coverage.json` plus a generated markdown
  view), not prose, so a test can read it.
- A test asserts every row marked `HANDLED` names a fixture that exists and that
  the fixture actually exercises that node type.

**Done when.** The matrix exists, every current gap in this plan appears in it as
`GAP`, and the study can cite it instead of asserting coverage.

**Size.** M. Mostly enumeration and judgement, little code.

### 0.2 Ledger unhandled constructs by default

**Problem.** `SkipLedger.Record` is called from exactly three places, all in
`CatalogBuilder` (lines 273, 352, 495). Everything else that falls through a
`switch` or a `when` guard vanishes. Concretely uncounted today:
`CatalogBuilder.cs:565` stores a column with `Type = null` and records nothing;
`CREATE ASSEMBLY` / `CREATE AGGREGATE` / `CREATE TYPE … EXTERNAL NAME` have no
visitor at all; `TypedPredicateExtractor.cs:374` returns an untyped operand for any
unrecognised expression without recording it. (A CLR table-valued function was
suspected to be a fourth silent drop via `ViewDefinitionExtractor.cs:46`'s `when`
guard - checked while implementing this item and found not to be one: a CLR TVF
still declares its `RETURNS TABLE(...)` column list in the script, so
`DeclareTableVariableBody.Definition` is never null there on a successful parse
and it resolves through the same path an ordinary multi-statement TVF does.)

The policy in `SkippedConstruct.cs:11-16` says nothing that reaches a pass is ever
silently dropped. That is currently aspirational.

**Work.**
- Give `ViewDefinitionExtractor.Extract` a ledger (it does not take one today) and
  add a default arm to its `switch`.
- Record at `CatalogBuilder.cs:565` when `SqlTypeReferenceResolver.Resolve` returns
  null, carrying the type name that could not be resolved.
- Record in `TypedPredicateExtractor.ResolveOperand`'s default arm, carrying the
  expression's node type name.
- Add explicit no-op-and-count visitors for `CreateAssemblyStatement`,
  `CreateAggregateStatement`, `CreateTypeUdtStatement`.
- Surface the ledger grouped by `ConstructKind` in `ScanReport` output, the way
  `DynamicSqlSummary` already is.

**Done when.** A scan of a fixture containing a spatial column, an unresolved
predicate operand and a `CREATE ASSEMBLY` reports three distinct, explainable
ledger entries; a test asserts the ledger is non-empty for each.

**Size.** M.

### 0.3 Crash-proof the trigger visitor

**Problem (unverified — reproduce first).** `TypedPredicateExtractor.cs:132,134`
dereference `node.TriggerObject.Name` unconditionally into
`SchemaObjectNameHelper.Qualify`, which dereferences `name.BaseIdentifier.Value`
(`SchemaObjectNameHelper.cs:11`). A DDL trigger (`ON DATABASE` / `ON ALL SERVER`)
and a LOGON trigger have no target object name. There is no try/catch anywhere in
`SilentScan.Core`, and `ScanReportBuilder.cs:60` calls the extractor per file with
no isolation — so one such file would abort an entire corpus scan rather than
degrade to a skip.

**Work.**
- Reproduce with a `CREATE TRIGGER … ON DATABASE FOR CREATE_TABLE` fixture. If it
  does not throw, downgrade this item to a coverage entry in the matrix and move on.
- Null-guard the target name; record a ledger entry naming the trigger scope and
  **still walk the body** — a DDL trigger body routinely contains ordinary
  analyzable predicates against real tables.
- Read `TriggerObject.TriggerScope` rather than inferring from a null name.
- Separately, decide whether `ScanReportBuilder` should isolate per-file failures.
  Recommendation: yes, but as its own item — a scan that dies on file 300 of 339
  loses everything, and no amount of null-guarding proves the next crash won't
  happen.

**Done when.** A DDL trigger and a LOGON trigger both scan without throwing, both
appear in the ledger, and predicates in their bodies against real tables are still
reported.

**Size.** S.

---

## Phase 1 — Wrong answers

Items that can produce a finding that is not true. Highest priority in the plan:
CLAUDE.md's precision-over-recall rule means one of these in the published study
is worse than every gap in Phase 3 combined.

### 1.1 Pseudo-table predicates must not inherit the base table's index

**Problem.** `BuildTriggerPseudoTableRelations` (`TypedPredicateExtractor.cs:214`)
binds `inserted`/`deleted` to the target table's `ResolvedRelation`, so
`inserted.Col` resolves to `ColumnProvenance.BaseColumn("dbo.Orders", "Col")` and
later inherits `Indexed: true` from `dbo.Orders`. There is no index on `inserted` —
it is a rowset materialised from the version store. A `ScanForced` + indexed
finding against a pseudo-table would rank **first** under the ranking rule in
CLAUDE.md while not being an index-killing conversion at all.

Note this is a defect *introduced by* the most recent commit (6534feb), which is
itself correct about types — the type inheritance is right, the index inheritance
is not.

**Work.** Requires a decision (see Decisions, D1). Under the recommended option:
- Carry a flag on the resolved relation marking it a pseudo-table.
- Report `Indexed: false` for predicates against it, with the reason recorded so the
  finding is still explicable.
- Keep the type resolution exactly as it is — the conversion is real and still
  costs CPU per row; only the seek-loss claim is wrong.

**Done when.** A fixture with `WHERE inserted.VarcharCol = @NvarcharParam` against a
target table with an index on `VarcharCol` reports the conversion but not
`Indexed: true`, and does not sort into the top rank band.

**Size.** S once D1 is decided.

### 1.2 Sweep for other index-attribution inheritance

**Problem.** 1.1 is an instance. Principle 4 requires checking the class: any place
that builds a `ResolvedRelation` from a catalog table for something that is not
that table. Candidates to check: table variables and temp tables (do they carry
inline index info correctly?), MSTVF declared shapes, and view-derived relations.

**Work.** Audit every `ToResolvedRelation` call site and every construction of
`ColumnProvenance.BaseColumn`; assert in a test that a qualified name reaching the
index lookup always denotes an object that can actually have an index.

**Done when.** The audit is written down in the coverage matrix rationale column,
and any discrepancy found is either fixed or recorded.

**Size.** S–M.

---

## Phase 2 — The statement-variant class fix

One systematic change rather than five one-off fixes. This is the class behind
question 2 and question 4 of the audit.

### 2.1 Create / Alter / CreateOrAlter parity across all three passes

**Problem.** ScriptDOM double-dispatch means `CreateX`, `AlterX` and
`CreateOrAlterX` are unrelated node types. Passes 1 and 3 handle all three for
procedures and functions. Nobody handles all three anywhere else:

| Construct | Pass 1 catalog | Pass 2 lineage | Pass 3 predicates |
|---|---|---|---|
| Procedure | all three | n/a | all three |
| Function | all three | **`Create` only** (`ViewDefinitionExtractor.cs:36,45`) | all three |
| View | n/a | **`Create` only** (`ViewDefinitionExtractor.cs:27`) | n/a |
| Trigger | `Create`/`Alter`, **no `CreateOrAlter`** (`CatalogBuilder.cs:250,252`) | n/a | `Create`/`Alter`, **no `CreateOrAlter`** (`TypedPredicateExtractor.cs:132,134`) |

The lineage row is the serious one: `CREATE OR ALTER VIEW` and `ALTER VIEW` produce
no `ViewDefinition`, so the view-inheritance analysis — the study's distinctive
claim — silently does not run on codebases that use the modern idiom.

**Work.**
- Extend `ViewDefinitionExtractor`'s switch to `AlterViewStatement`,
  `CreateOrAlterViewStatement`, `AlterFunctionStatement`,
  `CreateOrAlterFunctionStatement`, preserving last-definition-wins semantics
  within a scan (an `ALTER` after a `CREATE` stub must replace, not duplicate).
- Add `CreateOrAlterTriggerStatement` to `CatalogBuilder` and
  `TypedPredicateExtractor`.
- **Mechanical backstop:** a test that reflects over the ScriptDOM assembly, finds
  every `Create*Statement` for which an `Alter*` or `CreateOrAlter*` sibling exists,
  and asserts that each pass either handles all variants or names the construct in
  the coverage matrix with a rationale. This is what stops a sixth instance of this
  bug.

**Done when.** A view chain expressed entirely in `CREATE OR ALTER VIEW` produces
identical findings to the same chain in `CREATE VIEW`, and the reflection test
fails if a new variant is added without a matrix entry.

**Size.** M. The reflection test is the interesting part.

---

## Phase 3 — Coverage expansion

Ordered by value: how likely the construct is to appear in real T-SQL multiplied
by whether missing it costs a true finding.

### 3.1 Scalar UDF return types

**Problem.** `ScalarFunctionReturnType` appears nowhere in the codebase. There is
no function signature registry; `DatabaseCatalog` holds only tables and type
aliases. So `WHERE VarcharCol = dbo.fn_ReturnsNvarchar(@x)` resolves the right side
to `Type: null` (`TypedPredicateExtractor.cs:374`) and short-circuits to
`Verdict.Unknown` (`VerdictClassifier.cs:12-17`). This is the single highest-value
gap: a textbook index-killing pattern, guaranteed false negative, and until 0.2
lands not even counted.

**Work.**
- Add a function signature registry to Pass 1: qualified name → declared return
  type, for all three statement variants, scalar functions only.
- Resolve a `FunctionCall` operand against it in `ResolveOperand`; unknown function
  (including every builtin) stays `null` and now gets a ledger entry.
- Do **not** analyze UDF bodies to infer a return type — the declared type is
  authoritative and inference would violate the never-guess rule.
- Builtins are out of scope for v1 and recorded as such in the matrix. `LEN`,
  `GETDATE` and friends are a separate, larger table with their own precision risk.

**Done when.** A varchar column compared to an nvarchar-returning UDF reports
`ScanForced`, oracle-confirmed; the same column compared to a varchar-returning UDF
reports `SeekPreserved`; an unknown function still reports `Unknown` and is
ledgered.

**Size.** L. Largest single item in this plan.

### 3.2 Table types (`CREATE TYPE … AS TABLE`) and TVPs

**Problem.** `CreateTypeTableStatement` is handled nowhere. WWI's manifest
explicitly lists `Website/User Defined Types/*.sql` in `ddlPaths` — four files with
typed, indexed columns — and four WWI procs take them as `READONLY` TVPs
(`InsertCustomerOrders.sql:3-4`, `InvoiceCustomerOrders.sql:3`,
`RecordColdRoomTemperatures.sql:3`). The manifest declares coverage the tool does
not have, and nothing notices.

**Work.**
- Catalog `CREATE TYPE … AS TABLE` as a named column shape, including its inline
  `INDEX` and `PRIMARY KEY` definitions.
- Bind a procedure parameter declared with that type as a resolvable relation, so
  `FROM @Orders o WHERE o.Col = …` resolves.
- Table types genuinely *can* be indexed, so unlike 1.1 the index attribution here
  is real and should be reported.

**Done when.** A predicate against a TVP column resolves to the declared type, and
a WWI scan shows the four type files contributing to the catalog rather than being
silently inert.

**Size.** M.

### 3.3 INSTEAD OF triggers on views

**Problem.** `BuildTriggerPseudoTableRelations` resolves the target with
`catalog.Find` (`TypedPredicateExtractor.cs:216`), and `DatabaseCatalog` holds no
views — they live in `LineageCatalog.AllRelations`. For `CREATE TRIGGER … ON
dbo.SomeView INSTEAD OF INSERT`, every `inserted`/`deleted` predicate is dropped
with the misleading ledger reason `"has no known DDL"` while the view sits fully
resolved. This is the only relation-building site in the codebase that skips the
view check `FromScopeResolver.cs:164` performs.

**Work.** Consult `resolvedViews` — already a constructor field on the same class
(`TypedPredicateExtractor.cs:26,34`) — before falling back to `catalog.Find`. Fix
the ledger reason to distinguish "no such object" from "target is a view".
Interacts with 1.1: a view's `inserted` has no index either, so the pseudo-table
flag covers both.

**Done when.** An INSTEAD OF trigger on a view resolves `inserted.Col` to the view
column's type with correct lineage depth to the base column.

**Size.** S.

### 3.4 MSTVF return variable

**Problem.** `RETURNS @t TABLE(…)` is a `DeclareTableVariableBody` hanging off the
return type, not a `DeclareTableVariableStatement`, so `CatalogBuilder.cs:213-221`
never registers `@t`. Predicates inside the body over `FROM @t` are unresolvable.

**Work.** Register the return variable in the function's own temp-object scope,
reusing the scoping `CatalogBuilder.cs:239-249` already applies.

**Size.** S.

### 3.5 TVF-to-TVF dependency edges

**Problem.** `ViewDependencyGraph.TableReferenceCollector` collects only
`NamedTableReference` (`ViewDependencyGraph.cs:88-93`). An iTVF selecting `FROM
dbo.other_itvf(…)` creates no edge, so topological order can resolve the outer
function first and the inner one's columns degrade to Unknown. True cycles through
TVF calls are also not detected.

**Work.** Collect `SchemaObjectFunctionTableReference` too.

**Done when.** A two-deep iTVF chain resolves to base columns at depth 2 regardless
of declaration order in the file, and a TVF cycle is reported as cyclic rather than
Unknown.

**Size.** S.

### 3.6 CLR: decline explicitly, do not model

**Problem.** No CLR handling exists. Type inference degrades to `null` → `Unknown`,
so nothing is *wrong* — but nothing is counted either, and the study never mentions
it. `sys.geography` columns appear in 25 WWI files and are the concrete instance.

**Work.** Requires a decision (D2). Under the recommended option: no CLR analysis.
0.2 already makes these countable; this item only adds the matrix rows and the
study text. Explicitly out of scope: `CREATE ASSEMBLY` bodies, CLR UDT comparison
semantics, CLR aggregates.

**Size.** S, given 0.2.

---

## Phase 4 — Dynamic SQL parity

### 4.1 Propagate enclosing scope into analyzed dynamic SQL

**Problem.** Constant-proved dynamic SQL is reparsed as a standalone script, so
`_currentProcScope` is null (`TypedPredicateExtractor.cs:40`). A `#temp` declared
in the calling procedure resolves to the wrong shape or not at all, and trigger
`inserted`/`deleted` do not resolve inside dynamic SQL at all — meaning the
Phase 1.1 and 3.3 work does not reach that path.

**Work.** Thread the call site's proc/trigger scope through `DynamicSqlPipeline`
into the extractor. Types for outer `DECLARE`s remain deliberately unpropagated —
`DynamicSqlPipelineTests.cs:122` encodes that as intended, and it is correct: the
outer variable's value is not provably what reaches the inner batch.

**Size.** M.

### 4.2 Merge declared parameters through nesting

**Problem.** `DynamicSqlPipeline.cs:107` recurses with only the nested script's
`AnalyzableScripts`; an outer `sp_executesql` parameter used two levels down types
as Unknown.

**Size.** S.

### 4.3 Dynamic SQL test debt

**Problem.** CTEs inside dynamic SQL work by construction — the same
`TypedPredicateExtractor` visitor handles them (`TypedPredicateExtractor.cs:146-151`)
— but zero tests prove it. There is no `WITH` anywhere in the dynamic-SQL tests.
Behaviour that holds only by construction is behaviour that regresses silently.

**Work.** Add: a CTE inside `sp_executesql` resolving through lineage; a CTE
shadowing a real table inside dynamic SQL; a recursive CTE inside dynamic SQL
yielding Unknown.

**Size.** S.

### 4.4 Objects created inside dynamic SQL — record, do not resolve

**Problem.** `EXEC('CREATE TABLE #x …')` never enters the catalog, so subsequent
references are invisible.

**Work.** Ledger it. Do not attempt to resolve — feeding dynamically-created
objects back into a catalog that later passes have already consumed is a
re-entrancy problem well out of proportion to its value, and guessing violates the
never-guess rule. Recorded in the matrix as a deliberate exclusion.

**Size.** S.

---

## Phase 5 — Test debt with no code change

Items where behaviour is believed correct but unproven. Grouped so they can be done
in one sitting rather than blocking earlier phases.

- No fixture exercises `INSTEAD OF` at all (on table or view).
- No fixture exercises a multi-action `FOR INSERT, UPDATE` trigger.
- `TriggerType` (For/After/InsteadOf) is never read anywhere in `src/`; INSTEAD OF
  on a table works by omission rather than by design. Assert it.
- `UPDATE(col)` and `COLUMNS_UPDATED()` are correctly ignored — they are not
  comparison predicates. Record that as a deliberate exclusion in the matrix so the
  next reader does not re-investigate.

**Size.** M in aggregate.

---

## Phase 6 — Re-run and rewrite

### 6.1 Re-run the pilot

Every preceding phase changes what the tool reports. Re-run the 5-repo scan, re-run
the oracle confirmation, and diff against the numbers currently in `docs/study.md`.

Expected deltas, stated in advance so a surprise is a signal:
- Phase 2 should change almost nothing on this corpus (one `ALTER VIEW` in DNN,
  zero `CREATE OR ALTER`). If it changes a lot, something else is wrong.
- Phase 3.1 should *add* findings, all requiring fresh oracle confirmation.
- Phase 1.1 may *remove* findings, or reclassify their rank.
- Phase 0.2 will add substantial ledger volume without changing any verdict.

### 6.2 Rewrite the honest-caveats section

`docs/study.md:147-176` currently accounts for dynamic SQL and `UNKNOWN` typed
comparisons — the two buckets that happened to have counters — which reads as
complete accounting when it is accounting over what was instrumented. Replace with
a coverage statement that cites the matrix, states the deliberate exclusions
(builtins, CLR, dynamically-created objects), and reports the ledger by
`ConstructKind`.

Nothing published without Umang's explicit go-ahead, per CLAUDE.md.

---

## Decisions needed before starting

**D1 — Pseudo-table index semantics (blocks 1.1).**
`inserted.VarcharCol = @nvarchar` performs a real conversion but cannot lose a seek
that never existed. Options:
- (a) **Recommended.** Report the finding with `Indexed: false` and a recorded
  reason. Honest, keeps the CPU-cost signal, keeps it out of the top rank band.
- (b) Do not report predicates against pseudo-tables at all. Cleanest for the study,
  loses a real if lesser cost signal.
- (c) Split the schema: `BaseColumnIndexed` vs `IndexUsableHere`. Most correct,
  most invasive, changes the versioned findings schema and the SARIF mapping.

**D2 — CLR scope (blocks 3.6).** Recommended: count and decline, never analyze.
The alternative is modelling CLR UDT comparison semantics, which is a large amount
of work for a construct absent from the corpus and rare in the wild.

**D3 — Scalar UDF builtins (affects 3.1 size).** Recommended: user-defined
functions only in v1; builtins recorded as a known exclusion. Typing the builtin
surface is its own project and carries real false-positive risk.

**D4 — Per-file failure isolation in `ScanReportBuilder` (raised by 0.3).** Should
a scan survive a crash in one file? Recommended yes, as a standalone item — but it
trades a hard failure for a quiet one, so the ledger entry must be loud.

---

## Ordering summary

| Phase | Theme | Gate before proceeding |
|---|---|---|
| 0 | Matrix, default ledgering, crash-proofing | Matrix checked in; every plan item appears in it |
| 1 | Wrong answers | No path produces an index claim for a non-indexable relation |
| 2 | Statement-variant class fix | Reflection backstop test in place and failing on a seeded omission |
| 3 | Coverage expansion | Each new surface oracle-confirmed; matrix cell flipped |
| 4 | Dynamic SQL parity | Dynamic path reaches feature parity or records why not |
| 5 | Test debt | Every "works by construction" claim has a test |
| 6 | Re-run and rewrite | Deltas match the predictions in 6.1, or are explained |

## Notes on effort shape

Phase 3.1 (scalar UDFs) is the only genuinely large item and the only one that adds
a new subsystem. Phase 2's reflection backstop is small in code but is the item
with the highest long-term value — it is the thing that converts this plan from a
third round of one-off fixes into the last one. Phase 0.1 is judgement-heavy and
should not be delegated to a mechanical enumeration of ScriptDOM; the value is in
the exclusion rationales, not the row count.
