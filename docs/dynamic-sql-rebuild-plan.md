# Dynamic SQL core rebuild — implementation spec

> **Disposable document.** This is the handoff spec for one specific rebuild. It is the
> plan for the work, not a standing contract — CLAUDE.md remains the contract. **Delete
> this file in the Phase 6 commit.** If reality diverges from this spec mid-work, prefer
> reality, update the spec in place, and note the divergence in the commit message.

## Why (one paragraph)

`DynamicSqlScanner` models a variable's value as concrete literal-text segments and
re-implements T-SQL evaluation one special case at a time: ten `TryFold*` methods, five
builtin classification sets, hand-written IF/WHILE/TRY-CATCH handlers each with their own
merge logic, and unknown values retrofitted as `__silentscan_sym_LxCy__` tokens spliced
into text and patched around at parse time (`NeutralElisionVariant`, `symbolic-value-broke-parse`).
Every new corpus file finds the next unenumerated case. The fix: a symbolic **template**
value domain (literal / typed-hole / choice), one dataflow engine with a defined join and
a havoc-by-default transfer function, builtins as a declarative table, and hole rendering
owned by exactly one component. Sound by construction; special cases become data.

## What must NOT change

- `DynamicSqlScanner.Scan(SqlParseResult, DynamicSqlScope?, ProcCallGraph?, outputSummaries, DatabaseCatalog?)`
  → `DynamicSqlExtractionResult(Findings, AnalyzableScripts, OutputSummaries)`. Same
  signature, same semantics for all four optional inputs (call-graph parameter seeding,
  OUTPUT-summary seeding, single-table SELECT-assign column resolution, scope propagation).
- `DynamicSqlScript` record shape (`src/SilentScan.Core/Predicates/DynamicSqlScript.cs`):
  CallSite, InnerText, SegmentMap, ParameterDeclarationText, Scope, ArgumentBindings,
  Confidence, PlaceholderOccurrences. The pipeline consumes exactly this.
- `DynamicSqlPipeline`'s public `Analyze` and all its dedupe/remap/nesting/seeding logic
  (it only shrinks in Phase 5).
- `FindingConfidence` semantics: High = zero holes anywhere in the assembly; Medium = at
  least one hole; computed in ONE place (`BuildScript`), never assigned elsewhere.
  The standing invariant test `DynamicSqlPlaceholderConfidenceInvariantTests` must pass
  **unmodified** in every phase.
- CLAUDE.md soundness policy: never guess a runtime value; every declined call site gets a
  machine-readable reason; nothing is silently counted clean.
- Zero-issue gate before every commit: `pwsh ./sonar-scan.ps1` (never bare `dotnet build`;
  tests via `scripts/dotnet-safe.sh test`, scoped filters while iterating). Never run two
  dotnet invocations concurrently. Stage explicit paths, never `git add -A`.

## Target architecture

### 1. Value domain — `src/SilentScan.Core/Predicates/DynamicSqlValue/` (new folder)

All types immutable records. No public mutation, no text search against assembled strings.

```csharp
// SqlTextValue.cs — the lattice value for one variable
public abstract record SqlTextValue
{
    public sealed record Template(IReadOnlyList<TemplatePiece> Pieces) : SqlTextValue;
    public sealed record Tainted(string Reason, SourceSpan Location) : SqlTextValue;
}

// TemplatePiece.cs
public abstract record TemplatePiece
{
    // Real source text. Origin/PrefixLength carry exactly what DynamicSqlSegmentMap.AppendLiteral needs.
    public sealed record Lit(string Text, SourceSpan Origin, int PrefixLength) : TemplatePiece;

    // A value with a known (or null=unknown) SQL type but unknown content.
    public sealed record Hole(SqlType? Type, SourceSpan Origin, HoleKind Kind) : TemplatePiece;

    // Provable branch divergence, kept lazy until Expand(). GuardText is the canonical
    // rendering of the IF predicate that caused it ("" when unknown/join-produced) — it is
    // what lets a later `IF <same guard>` re-correlate (replaces GuardedAlternatives).
    public sealed record Choice(string GuardText, IReadOnlyList<SqlTextValue.Template> Alternatives) : TemplatePiece;
}

// HoleKind.cs — drives rendering + reason strings; extend freely, each member documented.
public enum HoleKind
{
    UntypedParameter,        // formal param, no known caller literal — type from declaration
    UninitializedDeclare,    // DECLARE @x nvarchar(50) with no/NULL initializer
    NonDeterministicTyped,   // NEWID/GETDATE/RAND/CHECKSUM/... — known return type
    EnvironmentDependent,    // SERVERPROPERTY/@@SERVERNAME/... (typed or untyped)
    HavocWrite,              // written by an unmodeled statement; type from declaration if known
    WidenedChoice,           // Choice collapsed by the cardinality cap
    OptionalFragment,        // stands for a whole optional clause, not a scalar → renders as space
}
```

Operations (static/instance methods on the records, each with unit tests):

- `Concat(SqlTextValue a, SqlTextValue b)` — `Tainted` absorbs (left one wins as the reason);
  `Template + Template` appends piece lists (merging adjacent `Lit`s from the same origin is
  NOT done — position mapping needs original boundaries). `Choice` is NOT distributed here;
  it stays a piece. Lazy is the point.
- `Join(SqlTextValue a, SqlTextValue b, string guardText, SourceSpan at)` — the ONE merge
  used by the CFG:
  1. structurally equal → `a`.
  2. both Templates → single-piece `Choice(guardText, [a, b])` template, subject to the cap
     (see Widen).
  3. else if both have an agreed single SqlType (see `TryGetUniformType`) →
     `Template([Hole(type, at, WidenedChoice)])`.
  4. else → `Tainted("diverges-in-control-flow-graph", at)`.
  A `Choice` joining with another `Choice` under the SAME `guardText` merges alternative
  lists (dedupe structurally) instead of nesting — this is what keeps fixpoints finite.
- `Widen(int maxAlternatives)` — any `Choice` whose recursive `ExpansionCount` exceeds
  `MaxAssembliesPerVariable` (keep 32, keep the constant name) collapses to
  `Hole(uniformTypeOrNull, at, WidenedChoice)`; if no uniform type, the whole value becomes
  `Tainted("diverges-across-if-branches:cardinality-cap", at)` — SAME reason string as today.
  Widening must be monotone: applied twice = applied once (test this).
- `Expand(int maxAssemblies)` → `IReadOnlyList<IReadOnlyList<FlatPiece>>` where
  `FlatPiece = Lit | Hole` (no Choice). Called EXACTLY once, in `BuildScript`. Cartesian
  product across Choice pieces, capped (cap already enforced by Widen before this runs, so
  Expand hitting the cap is an assertion failure, not a decline).
- `ContainsHole` → drives Confidence.

### 2. Renderer — `src/SilentScan.Core/Predicates/DynamicSqlValue/TemplateRenderer.cs`

Consumes one expanded assembly (`IReadOnlyList<FlatPiece>`), produces everything the
pipeline needs, and OWNS all position knowledge:

```csharp
public sealed record RenderedScript(
    string InnerText,
    DynamicSqlSegmentMap SegmentMap,                       // built here, nowhere else
    IReadOnlyList<PlaceholderOccurrence> Placeholders);    // empty when no holes

public static class TemplateRenderer
{
    // Render every Lit verbatim (AppendLiteral), every Hole as its token
    // "__silentscan_sym_L{line}C{col}__" via AppendPlaceholder — EXCEPT
    // HoleKind.OptionalFragment, which renders as a single space up front (no
    // failed-parse round trip needed; today's NeutralElisionVariant behavior, but chosen
    // from the HoleKind instead of reverse-engineered from a parse error).
    public static RenderedScript Render(IReadOnlyList<FlatPiece> assembly);

    // Fallback ladder for DynamicSqlPipeline when the token-rendered text fails to parse:
    // re-render with ALL scalar holes elided to spaces; if that parses, outcome
    // PartiallyAnalyzed/"optional-fragment-elided"; if not, Unanalyzable/
    // "symbolic-value-broke-parse". This REPLACES NeutralElisionVariant — the space
    // substitution happens at render time so DynamicSqlSegmentMap (which already collapses
    // any position inside a placeholder segment to its origin) is the ONLY mapping layer.
    public static RenderedScript RenderElided(IReadOnlyList<FlatPiece> assembly);
}
```

Hard rule: no component may locate a placeholder by searching `InnerText`. Offsets come
from `AppendPlaceholder`'s return value, as today.

### 3. Builtin table — `src/SilentScan.Core/Predicates/DynamicSqlValue/BuiltinRegistry.cs`

Replaces: `WhitelistedStringBuilders`, `NonDeterministicFunctions`,
`PlaceholderProducingNonDeterministicFunctions`, `EnvironmentDependentFunctions`,
`PlaceholderTypeTransfer`, and the code paths of `TryFoldStringBuilder`,
`TryFoldCaseConversion`, `TryFoldTrim`, `TryFoldLeftOrRight`, `TryFoldSubstring`,
`TryFoldReplace` (+ symbolic variant + `SpliceReplacementIntoTemplateParts`),
`TryFoldQuoteName`, `TryFoldCharOrNChar`, `TryFoldStr`, `TryFoldCastOrConvert`,
`TryTransferPlaceholderThroughFunction`.

```csharp
public sealed record BuiltinSpec(
    string Name,
    // All-literal args → concrete result, or a decline reason (e.g. today's
    // "non-literal-expression:char-out-of-range", "...:quotename-null-result",
    // "...:substring-start-below-one", "...:negative-length",
    // "...:str-length-out-of-range", "...:replace-empty-pattern",
    // "...:case-conversion-collation-sensitive", "...:replace-collation-sensitive",
    // "...:cast-target-not-pinned" — keep every existing guard and its exact string).
    Func<BuiltinCall, EvalResult>? Evaluator,
    // ≥1 hole arg → map the hole's type through (UPPER/LOWER/LTRIM/RTRIM/LEFT/RIGHT/
    // SUBSTRING preserve; STR→char; CAST/CONVERT→pinned char/nchar target; QUOTENAME→
    // nvarchar; today's PlaceholderTypeTransfer semantics, incl. REPLACE hole-splicing
    // becoming: literal template parts + the hole spliced between them).
    Func<BuiltinCall, EvalResult>? HoleTransfer,
    // Fallback when args are opaque but the builtin's return type is documented:
    // produce Hole(ReturnType, site, NonDeterministicTyped|EnvironmentDependent).
    SqlType? ReturnType,
    HoleKind ReturnKind);
```

Resolution order in the expression evaluator: all-Lit args → `Evaluator`; any Hole and
`HoleTransfer` defined → `HoleTransfer`; else `ReturnType` defined → typed Hole; else
`Tainted("non-literal-expression:function-call", site)`. Seed `ReturnType` rows for the
documented deterministic + nondeterministic scalar builtins in one sweep (GETDATE/
SYSDATETIME/SYSUTCDATETIME family, NEWID/NEWSEQUENTIALID, RAND, CHECKSUM/BINARY_CHECKSUM,
@@ functions, SERVERPROPERTY as EnvironmentDependent, LEN/DATALENGTH→int, CONCAT, FORMAT,
TRIM, TRANSLATE, REVERSE, SPACE, REPLICATE, STUFF, …). A row with only `ReturnType` is one
line; that is the whole point.

### 4. Dataflow engine — promote and generalize the existing CFG

Extract the `ControlFlowGraph` nested class (currently `DynamicSqlScanner.cs` ~line 3077,
with `Solve`/`RunFixpoint`/`RunFinalEmissionPass`/`MergeExitStates`) to
`src/SilentScan.Core/Predicates/DynamicSqlValue/DynamicSqlCfg.cs` and make it the ONLY
execution path — today it runs only for GOTO-bearing scopes; after this phase
`goto-or-label-in-scope` as a decline reason DIES (GOTO is just edges now) and
`HandleIf`/`HandleWhile`/`HandleTryCatch`/`MergeUnioningDivergent`/
`TryMergeFreshlyDeclaredInOneBranchOnly`/`CombineGuardedAlternatives`/
`ClearGuardedAlternatives`/`ResolveGuardedAlternatives`/`GuardedAlternative` are DELETED.

- State: `Dictionary<string, SqlTextValue>` (OrdinalIgnoreCase). Block-entry merge calls
  `SqlTextValue.Join` per variable; a variable present in only one predecessor state:
  if it was freshly declared in that branch only, join with
  `Hole(declaredType, declSite, HavocWrite)` (today's TryMergeFreshlyDeclaredInOneBranchOnly
  semantics, now a Join rule, incl. the TRY→CATCH seeding case).
- Lowering: IF → two-successor branch, guardText = canonical predicate text (reuse the
  existing script-generation of guard text); WHILE → back-edge (fixpoint + Widen handles
  it; the blanket `"while-loop-body"` taint DIES — a loop that only appends literals now
  widens to Choice→Hole instead of declining); TRY/CATCH → edge from try-entry AND each
  try-block boundary into catch (keep current conservatism: catch entry joins every
  intermediate try state); GOTO/labels → as the existing class already does; RETURN /
  THROW → no fallthrough successor.
- Transfer functions, in `DynamicSqlTransfer.cs`, one method per modeled statement:
  DECLARE (explicit NULL initializer = no initializer, keep), SET, SELECT-assign (keep
  `select-assignment-not-pure` + single-table catalog column resolution +
  `select-source-column-type-unresolved`), FETCH (typed holes for targets), EXECUTE
  (keep: output-summary seeding, `TaintExecuteMutatedVariables` semantics → holes/taint
  for OUTPUT args, and script emission — see below).
- **Default transfer for ANY unmodeled statement kind: HavocWrites** — run
  `WrittenVariableCollector` (keep it), each written variable becomes
  `Hole(declaredTypeOrNull, site, HavocWrite)` if its declared type is known, else
  `Tainted("unsupported-statement-in-scope", site)`. This must be the `default:` arm of
  the statement switch, so new/unknown ScriptDOM nodes are conservative automatically.
  Reads are never tainted by default (reading can't change a value).
- Script emission (EXEC/sp_executesql sites): keep the existing two-pass shape —
  fixpoint with `_suppressEmission`, then one emission-enabled pass — so each call site
  emits once with the settled state. `BuildScript` = fold arg → `Widen` → `Expand` →
  per assembly `TemplateRenderer.Render` → `DynamicSqlScript` (Confidence from
  `ContainsHole`; ParameterDeclarationText/ArgumentBindings logic unchanged).

### 5. Reason strings

Introduce `DynamicSqlDeclineReason.cs`: a static class of `const string`s — every string
currently emitted (full inventory below) becomes a named constant; tests reference
constants. Strings that survive keep their EXACT current spelling (JSON compatibility).
Strings that die, die in Phase 4 only, listed in the commit message:

- Survive: `non-literal-expression` + every `:suffix` variant, `variable-not-in-scope`,
  `environment-dependent-function`, `select-assignment-not-pure`,
  `select-source-column-type-unresolved`, `parameter-not-seeded:*`,
  `procedure-parameter:no-known-call-site`, `unsupported-assignment`,
  `unsupported-execute-form`, `unsupported-statement-in-scope`, `non-literal-argument`,
  `diverges-across-if-branches:cardinality-cap` (now emitted by Widen),
  `diverges-in-control-flow-graph`, `symbolic-value-in-function-argument` (only where no
  HoleTransfer/ReturnType row applies), pipeline reasons (`max-nesting-depth-exceeded`,
  `nested-dynamic-sql-inside-symbolic-value`, `optional-fragment-elided`,
  `symbolic-value-broke-parse`, `symbolic-value-not-positionable:whole-statement`,
  `template-placeholder-not-instantiated`).
- Die (replaced by analysis instead of decline): `goto-or-label-in-scope`,
  `while-loop-body`, `diverges-across-try-catch` (subsumed by
  `diverges-in-control-flow-graph`), `non-deterministic-function` (becomes a typed hole
  via ReturnType rows; keep the string ONLY for builtins with genuinely unknown type).

## Design rules (enforced in review, not aspirational)

1. Immutable value domain; state dictionaries copied at joins, never shared.
2. All merging goes through `SqlTextValue.Join`. A second merge implementation anywhere
   is a defect.
3. Unmodeled = conservative-by-default (HavocWrites). Never add a per-construct branch to
   regain precision without first writing the corpus-derived fixture that demands it.
4. Builtin knowledge lives ONLY in `BuiltinRegistry` rows. A `TryFold`-style method for a
   specific function is a defect.
5. All position knowledge flows forward through `TemplateRenderer`/`DynamicSqlSegmentMap`.
   Searching assembled text, or a second line/col↔offset implementation, is a defect.
6. No allow-list of "safe grammar positions" for holes — see the soundness argument in
   the comment block at `DynamicSqlPipeline.cs` (~line 269): a synthesized token can never
   resolve against the catalog, so it can only under-report, never fabricate.
7. Confidence derived from `ContainsHole` in `BuildScript` only.
8. Fixtures for new behavior come from real internet-sourced bugs (CLAUDE.md), named
   `RULEID_fires.sql`/`RULEID_clean.sql`, cleanup unconditional.

## Phases

Every phase ends with: scoped tests green (`scripts/dotnet-safe.sh test --filter
"FullyQualifiedName~DynamicSql"`), then full `pwsh ./sonar-scan.ps1` at 0 issues, then a
conventional commit (describe the change; never "phase N"). One logical unit per commit.

### Phase 0 — Characterization baseline

1. Run `scan-corpus` on the pinned 5-repo manifest; save full JSON output (findings +
   DynamicSqlSummary + per-reason counts) under the session scratchpad AND copy to
   `docs/.rebuild-baseline/` (gitignored — add the ignore entry; deleted in Phase 6).
2. Write `docs/.rebuild-baseline/diff-scans.sh`: compares two scan JSONs — findings keyed
   by (rule, source path, line, column, verdict, confidence), plus a decline-reason
   histogram diff. jq-based is fine.
3. Sanity: harness diffing the baseline against itself reports zero.

**Exit gate:** baseline + harness exist; self-diff is empty. No product code touched.
(Nothing here is committed except the .gitignore line.)

### Phase 1 — Value domain + renderer (standalone, old code untouched)

1. Implement `SqlTextValue`/`TemplatePiece`/`HoleKind`/`FlatPiece`, `Concat`/`Join`/
   `Widen`/`Expand`/`ContainsHole`/`TryGetUniformType`.
2. Implement `TemplateRenderer.Render`/`RenderElided`.
3. Tests (new file `DynamicSqlValueTests.cs`):
   - Concat: associativity; Tainted absorption; Choice preserved un-distributed.
   - Join: equal→same; guard-matched Choice-merge dedupes; type-agreeing divergence →
     WidenedChoice hole; disagreeing → Tainted with `diverges-in-control-flow-graph`.
   - Widen: idempotent; monotone; exact reason string on the no-uniform-type path.
   - Expand: cartesian correctness on nested Choices; count == product of alternatives.
   - Renderer: for every offset in rendered text, `SegmentMap.Map` returns the origin of
     the piece that produced it — port the multi-line-literal and `''`-escape cases from
     `DynamicSqlSegmentMapTests` as renderer-level tests; hole positions collapse to hole
     origin; OptionalFragment renders as one space; `RenderElided` spaces every hole.

**Exit gate:** all new tests green; sonar 0; commit. Old scanner byte-identical.

### Phase 2 — Builtin registry (standalone)

1. Implement `BuiltinRegistry` + `BuiltinSpec`, porting EVERY guard and reason string from
   the ten `TryFold*` methods (the `:suffix` inventory above is the checklist — each
   suffix maps to one Evaluator guard). Port `IsSafeToCaseConvert`, `QuoteName`,
   STR edge rules, CAST/CONVERT pinned-target rules exactly.
2. Add ReturnType rows for the documented builtin sweep (list in §3 above).
3. Tests (`BuiltinRegistryTests.cs`), table-driven:
   - For every row with an Evaluator: at least one literal-args case with expected output
     AND every decline guard hit once (steal expected values from existing
     `DynamicSqlScannerTests` cases).
   - For every row with HoleTransfer: hole-in → expected hole-type-out.
   - For every ReturnType row: opaque args → Hole of that type/kind.
4. Oracle spot-check (standing Docker infra, no permission needed): for ~10 evaluator
   rows, `SELECT <expr>` with literal args via the Verify harness and assert our folded
   string equals the engine's. Especially REPLACE/STR/QUOTENAME/CHAR edge cases.

**Exit gate:** registry covers 100% of the functions the old code handled (assert by
listing: UPPER LOWER LTRIM RTRIM SUBSTRING REPLACE LEFT RIGHT QUOTENAME CHAR NCHAR STR
ISNULL COALESCE CAST CONVERT + the nondeterministic/environment sets); tests + oracle
green; sonar 0; commit. Old scanner still untouched.

### Phase 3 — Engine unification (`DynamicSqlScannerV2`, old kept alive)

Build order inside the phase:

1. **Harness first**: `DynamicSqlScannerParityTests.cs` — a fixture runner that executes
   every existing scenario from `DynamicSqlScannerTests`, `DynamicSqlScopePropagationTests`,
   `DynamicSqlCrossCallEdgePipelineTests`, `DynamicSqlNestedParameterBindingPipelineTests`,
   `DynamicSqlParameterAliasPipelineTests` against BOTH `Scan` implementations and
   compares `DynamicSqlExtractionResult`s (scripts by (CallSite, InnerText, Confidence,
   ParameterDeclarationText, placeholder count); findings by (Line, Column, Outcome,
   Reason)). Mechanically: refactor those test classes to take the scan function as a
   parameter (theory data), don't duplicate the scenarios.
2. Extract + port `DynamicSqlCfg` to the `SqlTextValue` state domain.
3. Lower IF/WHILE/TRY-CATCH/GOTO/cursor scopes into the CFG; implement `DynamicSqlTransfer`
   (DECLARE/SET/SELECT-assign/FETCH/EXECUTE + HavocWrites default).
4. Expression evaluator: literals, variable refs, `+` concat, `BuiltinRegistry` dispatch,
   parameter/call-graph/output-summary seeding (port `BuildParameterSeed`,
   `SeedFromSingleEdge`/`SeedFromMultipleEdges`, `SeedKnownOutputArguments`,
   `TryFoldSelectSourceColumn` semantics), `FailNonLiteralExpression` reason mapping.
5. `BuildScript` via `Widen`→`Expand`→`TemplateRenderer`.

Divergence policy for the parity harness: V2 must be equal or **strictly better** per
scenario. Allowed improvement direction ONLY: declined→analyzed-at-Medium, or
Tainted→typed-Hole. Forbidden: analyzed→declined, High→Medium on a hole-free case, ANY
new High through a placeholder, any changed finding line/column. Each allowed divergence
gets the scenario's expectation updated with a comment naming the improvement (e.g.
"WHILE body no longer blanket-taints; widens instead").

**Exit gate:** parity harness green across every scenario with all divergences reviewed;
`DynamicSqlPlaceholderConfidenceInvariantTests` green against V2; sonar 0; commit
(V2 internal, not yet wired).

### Phase 4 — Cutover, deletion, corpus diff

1. Point `DynamicSqlScanner.Scan` at the V2 core. Delete: old `Visitor`, `FoldState`,
   `FoldAttempt`, `LiteralSegment`, `GuardedAlternative` + all Combine/Clear/Resolve
   methods, all `TryFold*`, the five builtin sets, `MergeUnioningDivergent`,
   `TryMergeFreshlyDeclaredInOneBranchOnly`, `SpliceReplacementIntoTemplateParts`,
   `TaintReferencedVariables` (if unreferenced after HavocWrites). Target:
   `DynamicSqlScanner.cs` < 400 lines (entry point + emission glue).
2. Full corpus scan → `diff-scans.sh` against the Phase 0 baseline. Review EVERY diff
   line: new finding = previously-declined site now analyzed (verify by locating its old
   decline reason in the baseline); lost finding = must be provably a prior false
   positive, else it is a V2 bug — fix before proceeding. Reason-histogram:
   `goto-or-label-in-scope`, `while-loop-body`, `diverges-across-try-catch`,
   cardinality-cap counts drop; no reason count may RISE except by documented reason-
   renaming.
3. `verify-corpus`: every ScanForced finding (old and new) oracle-confirmed via
   SHOWPLAN_XML. Any unconfirmed new finding is a P0 — precision beats recall.
4. Full test suite (not just the DynamicSql filter) + sonar 0.

**Exit gate:** zero unexplained corpus diffs; oracle confirms all ScanForced; suite +
sonar clean; commit (one commit for cutover+deletion; message summarizes the corpus
delta numbers and dead reason strings).

### Phase 5 — Pipeline simplification (pure refactor)

1. Delete `NeutralElisionVariant` and `TryReparseWithNeutralElision`; the pipeline's
   parse-failure path calls `TemplateRenderer.RenderElided` on the stored assembly
   (thread the `IReadOnlyList<FlatPiece>` through `DynamicSqlScript` as an
   internal-carrying member or a side-channel — pick the smallest change that keeps the
   public record shape) and re-parses once. Outcome/reason strings unchanged.
2. `TryParseAndClassify` shrinks accordingly; `IsEntirelyPlaceholder` becomes a template
   query (all pieces are Holes/whitespace Lits) instead of string surgery.

**Exit gate:** `DynamicSqlPipelineTests` + invariant tests green; corpus scan
**byte-identical** to Phase 4 output (this phase changes no behavior); sonar 0; commit.

### Phase 6 — Contract + cleanup

1. Rewrite CLAUDE.md's "Dynamic SQL" section to state (present tense, no history): values
   are literal/typed-hole/choice templates over a single CFG dataflow engine with a
   defined join and widening; unknown-but-typed degrades to Medium-confidence holes, only
   untyped/structural failures decline; unmodeled statements go through havoc-writes by
   default; builtin knowledge is registry rows; new precision requires a corpus-derived
   fixture and lands as data/transfer functions, never walker branches; High confidence
   never passes through a hole.
2. Delete `docs/.rebuild-baseline/` + its gitignore line, and DELETE THIS FILE.
3. Final full suite + sonar 0; commit.

**Exit gate:** repo contains no rebuild scaffolding; CLAUDE.md current-state only.

## Sequencing / effort

Phases 0–2 are independent of the engine and individually shippable; do them first and
commit each. Phase 3 is the bulk (~60% of the work) — build the parity harness before any
engine code. Phases 4–6 are mechanical if 3's gate was honest. If a session ends
mid-phase, land only completed sub-steps that keep sonar at 0; note the resume point in
the last commit message, not in a new markdown file.
