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

### 0.3 Crash-proof the trigger visitor — DONE, reproduced real

**Problem (was unverified; reproduced before fixing).** `TypedPredicateExtractor.cs:132,134`
dereferenced `node.TriggerObject.Name` unconditionally into
`SchemaObjectNameHelper.Qualify`, which dereferences `name.BaseIdentifier.Value`
(`SchemaObjectNameHelper.cs:11`). A DDL trigger (`ON DATABASE` / `ON ALL SERVER`)
and a LOGON trigger have no target object name. There is no try/catch anywhere in
`SilentScan.Core`, and `ScanReportBuilder.cs:60` calls the extractor per file with
no isolation — so one such file aborted an entire corpus scan rather than degrade
to a skip. Confirmed with a standalone repro before touching any code: both a
`CREATE TRIGGER … ON DATABASE FOR CREATE_TABLE` and a `… ON ALL SERVER FOR LOGON`
threw `NullReferenceException` from exactly this call chain.

**Work done.**
- Null-guarded the target name (`triggerObject.Name is not { } targetTableName`);
  records a ledger entry (`"DDL/LOGON trigger"`) naming the `TriggerScope` and
  **still walks the body** — a DDL trigger body routinely contains ordinary
  analyzable predicates against real tables, and the fix is verified to still
  report those.
- Read `TriggerObject.TriggerScope` for the ledger message rather than inferring
  from a null name — `VisitTriggerBody` now takes the whole `TriggerObject`.
- `CreateOrAlterTriggerStatement` deliberately NOT added here — it needs the same
  fix, but belongs with the rest of the `Create`/`Alter`/`CreateOrAlter` parity
  sweep in Phase 2.1, not bolted onto a crash fix.

**Still open.** Whether `ScanReportBuilder` should isolate per-file failures.
Recommendation unchanged: yes, but as its own item — a scan that dies on file 300
of 339 loses everything, and no amount of null-guarding proves the next crash
won't happen.

**Done when.** A DDL trigger and a LOGON trigger both scan without throwing, both
appear in the ledger, and predicates in their bodies against real tables are still
reported.

**Size.** S.

---

## Phase 1 — Wrong answers

Items that can produce a finding that is not true. Highest priority in the plan:
CLAUDE.md's precision-over-recall rule means one of these in the published study
is worse than every gap in Phase 3 combined.

### 1.1 Pseudo-table predicates must not inherit the base table's index — DONE

**Problem.** `BuildTriggerPseudoTableRelations` (`TypedPredicateExtractor.cs:214`)
bound `inserted`/`deleted` to the target table's `ResolvedRelation`, so
`inserted.Col` resolved to `ColumnProvenance.BaseColumn("dbo.Orders", "Col")` and
inherited `Indexed: true` from `dbo.Orders`. There is no index on `inserted` — it
is a rowset materialised from the version store. A `ScanForced` + indexed finding
against a pseudo-table would have ranked **first** under the ranking rule in
CLAUDE.md while not being an index-killing conversion at all.

Note this was a defect *introduced by* the most recent commit at the time
(6534feb), which was itself correct about types — the type inheritance was right,
the index inheritance was not.

**Decided and done, chosen option:** `FromScopeResolver.ToPseudoTableRelation`
gives pseudo-table columns `ColumnProvenance.Declared` instead of `BaseColumn` -
the identical "known type, never guess an index" treatment a multi-statement
TVF's declared `RETURNS TABLE(...)` column already gets (`Indexed: false` falls
out of the existing `Declared` branch in `ResolveColumnOperand` with no new
special-casing there), reusing an established pattern rather than adding a new
"pseudo-table flag" concept. Type resolution is unchanged — the conversion is
real and still costs CPU per row; only the seek-loss claim changes.
`TableQualifiedName` is kept as the real target table's name, so a finding
against `inserted`/`deleted` stays attributable to where the data actually lives.

**Verified.** A fixture with an indexed column on the target table, predicated
through `inserted`, reports the conversion with `Indexed: false`; the equivalent
direct `FROM dbo.Orders` predicate in the same test still reports `Indexed: true`,
proving the ordinary FROM-clause path is unaffected.

**Size.** S, as estimated.

### 1.2 Sweep for other index-attribution inheritance — DONE, no further defects found

**Problem.** 1.1 is an instance. Principle 4 requires checking the class: any place
that builds a `ResolvedRelation` from a catalog table for something that is not
that table.

**Audit result.** `ColumnProvenance.BaseColumn` (the only provenance kind that
feeds the real index lookup at `TypedPredicateExtractor.cs:502`) is constructed in
exactly one place in the whole codebase: `FromScopeResolver.ToResolvedRelation`,
used only for genuine `NamedTableReference` FROM-clause resolution. Every other
relation-building path (MSTVF declared shapes, view-derived relations, and now
trigger pseudo-tables) already produces `Declared`, `Expression`, `Cast`, `Union`,
or `Unknown` — none of which claim a real index. No further defect found; nothing
else to fix.

**Size.** S — the grep-based audit was the whole cost; no code changed beyond 1.1.

---

## Phase 2 — The statement-variant class fix

One systematic change rather than five one-off fixes. This is the class behind
question 2 and question 4 of the audit.

### 2.1 Create / Alter / CreateOrAlter parity across all three passes — DONE

**Problem.** ScriptDOM double-dispatch means `CreateX`, `AlterX` and
`CreateOrAlterX` are unrelated node types. Passes 1 and 3 handled all three for
procedures and functions. Nobody handled all three anywhere else:

| Construct | Pass 1 catalog | Pass 2 lineage | Pass 3 predicates |
|---|---|---|---|
| Procedure | all three | n/a | all three |
| Function | all three | was **`Create` only** | all three |
| View | n/a | was **`Create` only** | n/a |
| Trigger | was `Create`/`Alter` only | n/a | was `Create`/`Alter` only |

The lineage row was the serious one: `CREATE OR ALTER VIEW` and `ALTER VIEW`
produced no `ViewDefinition`, so the view-inheritance analysis — the study's
distinctive claim — silently didn't run on codebases using the modern idiom.

**Work done.**
- `ViewDefinitionExtractor`'s switch now matches `AlterViewStatement`,
  `CreateOrAlterViewStatement`, `AlterFunctionStatement`,
  `CreateOrAlterFunctionStatement` on the same shape as their `Create` forms.
  Last-definition-wins was already implemented in
  `ViewDependencyGraph.TopologicalSort` (a prior phase, for repeated `CREATE VIEW`
  across incremental-upgrade scripts) - reused unchanged, no new dedup logic
  needed.
- `CreateOrAlterTriggerStatement` added to `CatalogBuilder` and
  `TypedPredicateExtractor`.
- **Mechanical backstop built:** `StatementVariantParityTests` reflects over the
  real ScriptDOM assembly (not a hand-written list), finds every concrete
  `CreateXStatement` a pass handles that has an `AlterX`/`CreateOrAlterX` sibling,
  and fails if the sibling is neither handled nor named in the coverage matrix.
  Two bugs surfaced *while building the backstop itself*, both fixed before
  trusting it: (1) `GetMethods()` without `BindingFlags.DeclaredOnly` returns
  every inherited no-op `ExplicitVisit` overload `TSqlFragmentVisitor` itself
  declares, making every statement kind look "handled" regardless of whether the
  pass actually overrides it — caught by deliberately disabling a real override
  and watching the assertion still pass; (2) a stale `Gap` matrix row for
  `CreateOrAlterTriggerStatement` (citing a defect already fixed) let the
  matrix-fallback excuse a real code gap — fixed by only honoring the fallback
  for rows that currently claim `Gap`/`Ledgered`, never `Handled`.
- The backstop found two previously-unknown gaps beyond anything in this plan:
  `AlterAssemblyStatement` and `AlterIndexStatement` were both silently unhandled
  in `CatalogBuilder`. `AlterTableStatement` also matched the sibling pattern but
  is abstract (ScriptDOM's ALTER TABLE concrete types are named differently -
  `AlterTableAddTableElementStatement` etc. - and were already handled); excluded
  abstract types from the check as a class fix, not a one-off skip. `ALTER
  ASSEMBLY` now gets the same CLR decline-to-model ledger entry as `CREATE
  ASSEMBLY`. `ALTER INDEX` is ledgered, not fixed - `DISABLE` makes a previously-
  seekable index genuinely unusable, which this pass can't yet track (needs
  index-name -> state, not just column-level index presence); real risk judged
  low since `REBUILD`/`REORGANIZE`, the common cases, don't change seekability.

**Verified.** A view chain expressed entirely in `CREATE OR ALTER VIEW` produces
identical resolution to the same chain in `CREATE VIEW`. The reflection backstop
was confirmed to actually fail on a deliberately-injected gap, not just checked to
pass — see the two self-caught bugs above.

**Size.** M, as estimated. The reflection test was indeed the interesting part -
and paid for itself immediately by finding two gaps nobody had listed.

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

### 3.3 INSTEAD OF triggers on views — DONE

**Problem.** `BuildTriggerPseudoTableRelations` resolved the target with
`catalog.Find`, and `DatabaseCatalog` holds no views — they live in
`LineageCatalog.AllRelations`. For `CREATE TRIGGER … ON dbo.SomeView INSTEAD OF
INSERT`, every `inserted`/`deleted` predicate was dropped with the misleading
ledger reason `"has no known DDL"` while the view sat fully resolved. This was the
only relation-building site in the codebase that skipped the view check
`FromScopeResolver`'s own `NamedTableReference` case performs.

**Work done.** `resolvedViews` (already a constructor field) is consulted first;
falls back to `catalog.Find` only when the target isn't a resolved view either,
and the ledger reason now says "no known DDL and is not a resolved view" so the
two cases are distinguishable. Interacted with 1.1 exactly as predicted: a view's
`inserted` has no index either, so the same `Declared`-provenance treatment
applies — but a view relation can carry `ColumnProvenance.BaseColumn` for a column
that passes an indexed base column straight through (correct for an ordinary
`SELECT` against the view, wrong for `inserted`/`deleted`), which the table case's
`ToPseudoTableRelation(CatalogTable?, string)` never had to handle. Added a second
overload, `ToPseudoTableRelation(ResolvedRelation, string)`, that downgrades any
top-level `BaseColumn` to `Declared` before reuse - the one new piece of logic this
item needed beyond wiring.

**Verified.** An INSTEAD OF trigger on a view resolves `inserted.Col` to the view
column's type, attributed to the view's own qualified name (the trigger's literal
target, not chased through to the ultimate base table - consistent with how the
table case already attributes to its own literal target). A second test confirms
the fix doesn't just work but stays honest: an indexed base column passed straight
through the view still reports `Indexed: false` for `inserted`.

**Size.** S, as estimated.

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

**D1 — Pseudo-table index semantics (blocked 1.1) — DECIDED: option (a).**
`inserted.VarcharCol = @nvarchar` performs a real conversion but cannot lose a seek
that never existed. Chose (a): report the finding with `Indexed: false`, keeping
the conversion/CPU-cost signal and keeping it out of the top rank band, over (b)
suppressing the finding entirely (loses a real signal) or (c) splitting the schema
into `BaseColumnIndexed`/`IndexUsableHere` (more correct but invasive - changes the
versioned findings schema and SARIF mapping for one narrow case). Implemented via
`ColumnProvenance.Declared` rather than a new flag (see 1.1).

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
