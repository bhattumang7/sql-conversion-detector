using System.Text.Json.Serialization;

namespace SilentScan.Core.Predicates;

public enum ControlFlowRiskFindingKind
{
    /// <summary>A <c>FETCH ... INTO</c> variable list whose count differs from the statically
    /// countable column count of its own cursor's defining <c>SELECT</c> - oracle-confirmed a real,
    /// always-reproducible runtime error (Msg 16924, "Cursorfetch: The number of variables declared
    /// in the INTO list must match that of selected columns"), not merely a style complaint. Only
    /// fires when the cursor's own defining SELECT is a simple, non-<c>*</c>, non-set-operator query
    /// specification whose own column count is directly countable from the parse - a <c>SELECT *</c>
    /// or <c>UNION</c>-shaped cursor source declines rather than guesses.</summary>
    CursorFetchColumnCountMismatch,

    /// <summary>A <c>BEGIN CATCH ... END CATCH</c> block with zero statements - every error reaching
    /// it is silently swallowed with no re-throw, no logging, nothing observable at all.</summary>
    EmptyCatchBlock,

    /// <summary>A <c>SELECT</c> with a real (non-assignment-only) result set, or a <c>PRINT</c>,
    /// appearing directly inside a <c>CREATE/ALTER TRIGGER</c> body - sends output back to whatever
    /// connection happened to fire the triggering DML statement, not the application code that
    /// issued it, a well-documented "output is silently ignored or confuses whatever tool is
    /// connected" antipattern. A <c>SELECT @x = expr</c>/<c>SELECT ... INTO</c> assignment-only form
    /// sends no client-visible result set and never fires. Only a trigger's own top-level body is
    /// inspected - a statement inside a procedure the trigger merely calls is not chased, since this
    /// pass never holds every module's parsed AST alive simultaneously (the same constraint
    /// documented for the SET-options stream's own reachable-object walk).</summary>
    TriggerEmitsOutput,

    /// <summary>A <c>NOLOCK</c>/<c>READUNCOMMITTED</c> table hint, or <c>SET TRANSACTION ISOLATION
    /// LEVEL READ UNCOMMITTED</c> - allows dirty reads (uncommitted, possibly-rolled-back data) and,
    /// less widely known, can silently miss or double-count rows during a concurrent page split.
    /// Reported as advisory, not an error: sometimes a deliberate, reasonable tradeoff for a
    /// reporting/analytics workload, not always a bug.</summary>
    DirtyReadIsolationHint,

    /// <summary>The same non-literal expression (a variable, column reference, or complex
    /// expression - a bare literal is deliberately excluded, since repeating <c>NULL</c>/<c>0</c>/an
    /// empty string across several optional arguments is completely normal and not suspicious)
    /// passed as two different arguments to the same <c>EXEC</c> or function call - a well-documented
    /// copy-paste-bug smell (one argument was very likely meant to reference something else).
    /// <c>FORMATMESSAGE</c> is excluded, since deliberately repeating a format substitution value
    /// across multiple positions is its own normal, intended usage.</summary>
    DuplicatedCallArgument,

    /// <summary><c>@@IDENTITY</c> referenced anywhere - returns the last identity value inserted in
    /// the CURRENT SESSION across ANY table and ANY scope, including a value inserted by a trigger
    /// fired as a side effect of the statement that ran just before it, a well-documented, sharp
    /// correctness trap (returns the WRONG identity value silently, no error raised). This pass
    /// cannot prove a trigger-caused collision is actually present for any specific reference, only
    /// that the intrinsic itself is inherently trap-prone - worded as "prefer SCOPE_IDENTITY()
    /// unless that broader semantics is specifically wanted," never as a definite bug.</summary>
    LegacyIdentityIntrinsic,

    /// <summary>A <c>GOTO</c> statement anywhere - unrestricted jumps make control flow harder to
    /// follow and, load-bearing for a real, separate consequence: <see cref="DeadCodeScanner"/>
    /// already declines its ENTIRE reachability analysis (unreachable code, unused labels/
    /// variables/parameters, redundant jumps) for the whole routine the moment it contains any
    /// <c>GOTO</c> at all - so a <c>GOTO</c>-using routine silently loses that whole other stream's
    /// coverage today, with nothing surfacing the reason why. This finding is the first thing in
    /// this codebase to actually SURFACE a <c>GOTO</c>'s presence as its own reportable fact, rather
    /// than only ever consuming it as an internal "give up" signal.</summary>
    GotoUsage,

    /// <summary>A simple <c>CASE &lt;input&gt; WHEN v1 THEN ... WHEN v2 THEN ... END</c> with no
    /// <c>ELSE</c> - oracle-confirmed directly (a real executed <c>SELECT</c> with no matching
    /// <c>WHEN</c> value and no <c>ELSE</c>): the expression silently evaluates to <c>NULL</c>, no
    /// error, no warning. The searched-CASE form (<c>CASE WHEN cond THEN ...</c>) has the identical
    /// fallthrough-to-NULL behavior but is deliberately NOT included here - a searched CASE's own
    /// boolean conditions are typically deliberately partial/mutually-exclusive-by-design (unlike a
    /// simple CASE's fixed, enumerable value list, where "did I forget a value" is the far sharper,
    /// far more common real mistake) - narrowing to the simple-CASE form only is what keeps this a
    /// high-precision finding rather than firing on every ordinary partial searched CASE in the
    /// corpus.</summary>
    CaseExpressionMissingElse,

    /// <summary>A non-deterministic function (<c>NEWID()</c>/<c>RAND()</c>/
    /// <c>CRYPT_GEN_RANDOM()</c>) appearing anywhere inside a simple CASE expression's own INPUT
    /// expression - oracle-confirmed directly, load-bearing and genuinely surprising: captured real
    /// compiled plan XML shows the optimizer rewrites <c>CASE NEWID() WHEN v1 THEN r1 WHEN v2 THEN
    /// r2 ELSE r3 END</c> into a NESTED <c>CASE WHEN newid()=v1 THEN r1 ELSE CASE WHEN newid()=v2
    /// THEN r2 ELSE r3 END END</c> - three SEPARATE <c>Intrinsic FunctionName="newid"</c> call sites
    /// in the real scalar-operator tree, not one evaluation reused across the comparisons. Confirmed
    /// this is a genuine per-call re-evaluation, not merely a repeated textual reference to one
    /// cached value: three bare <c>RAND()</c> references in a single real executed <c>SELECT</c>
    /// list independently returned three DIFFERENT values (this codebase's own separately-documented
    /// "RAND() folds to one constant across MULTIPLE ROWS of one query" finding, elsewhere in this
    /// file, is a different claim about row-invariance across a result set, not about multiple
    /// distinct textual call sites within one row's own expression evaluation - both are real,
    /// neither contradicts the other). Practical consequence: for a large-domain function
    /// (<c>NEWID()</c>/<c>CRYPT_GEN_RANDOM()</c>) every <c>WHEN</c> branch becomes, in effect,
    /// permanently unreachable dead code - the astronomically improbable event of one fresh random
    /// call matching a fixed literal - so the whole CASE structure silently always evaluates to its
    /// <c>ELSE</c> (or <c>NULL</c>, if it has none, compounding with the sibling
    /// <see cref="CaseExpressionMissingElse"/> finding when both apply to the same expression).
    /// Distinct from the already-probed-and-killed "non-foldable nondeterministic intrinsic in a
    /// predicate" item elsewhere in this file - that one was about seek/scan behavior in a WHERE
    /// predicate and was correctly found NOT to hold; this is a structurally different claim about a
    /// CASE expression re-evaluating its own input, and was independently oracle-confirmed true.</summary>
    NonDeterministicCaseInput,
}

/// <summary>
/// docs/detection-checklist.md Tier 4 "Cursor and control-flow correctness" (the first six members)
/// plus the "GOTO usage" and "non-deterministic function as CASE input" bullets from the same Tier 4
/// entry's small grab-bag list (the final three members) - grouped here rather than under a separate
/// finding type, since all nine are the same "AST-visible control-flow/expression-evaluation risk,
/// no catalog, no plan oracle" shape. "An output parameter never assigned" (a member of the original
/// "Cursor and control-flow correctness" bullet) is already fully shipped as
/// <see cref="OutputParameterFinding"/> (a path-sensitive "assigned on every return path" analysis, a
/// strict superset of the simpler "never assigned at all" case this Tier 4 entry names) - not rebuilt
/// here, cross-referenced in the checklist instead. The "dangling IF on a shared line" half of the
/// "Missing/ambiguous ELSE" bullet is already fully shipped as
/// <see cref="FormattingFindingKind.IfImmediatelyFollowingPriorBlockEnd"/>/
/// <see cref="FormattingFindingKind.SingleLineConditionalBody"/> - likewise cross-referenced, not
/// rebuilt. The "sibling inconsistency" half of that same bullet (some IFs in a routine have an ELSE,
/// others don't) was investigated and NOT shipped - too common and unopinionated a shape in ordinary
/// T-SQL to state as a real defect signal without excessive noise; <see
/// cref="ControlFlowRiskFindingKind.CaseExpressionMissingElse"/> ships instead as the sharper, real,
/// oracle-confirmed claim the checklist's own vaguer framing was gesturing at.
///
/// One finding type, one <c>Kind</c> discriminator - this codebase's established shared-plumbing
/// shape (matching <see cref="DeadCodeFinding"/>/<see cref="StatementShapeFinding"/>). Pure AST
/// checks throughout - no catalog needed for any member. No plan-XML oracle applies to any of these
/// (none make a plan-shape claim); <see
/// cref="ControlFlowRiskFindingKind.CursorFetchColumnCountMismatch"/>'s own real-runtime-error claim
/// was directly confirmed by executing the failing shape against the Docker instance rather than
/// assumed, the same self-authored-probe discipline <see cref="TempTableExecShapeFinding"/> already
/// uses for an analogous "this call shape provably fails" claim.
///
/// Confidence: <see cref="FindingConfidence.High"/> for the structurally-unambiguous, hard-fact kinds
/// (<see cref="ControlFlowRiskFindingKind.CursorFetchColumnCountMismatch"/> - a real, always-
/// reproducible runtime error; <see cref="ControlFlowRiskFindingKind.EmptyCatchBlock"/> - an
/// unambiguous zero-statement fact). <see cref="FindingConfidence.Medium"/> for the real-but-
/// context-dependent risks (<see cref="ControlFlowRiskFindingKind.TriggerEmitsOutput"/>, <see
/// cref="ControlFlowRiskFindingKind.DuplicatedCallArgument"/>, <see
/// cref="ControlFlowRiskFindingKind.LegacyIdentityIntrinsic"/> - each names a real, well-documented
/// trap, but none is provably a bug in isolation without runtime information this pass cannot see).
/// <see cref="FindingConfidence.Low"/> for <see cref="ControlFlowRiskFindingKind.DirtyReadIsolationHint"/>
/// specifically - a deliberate, common, sometimes-reasonable tradeoff, not a default-bad choice. SARIF
/// Warning throughout except the two High-confidence kinds, which are Error (a provably-wrong-outcome
/// claim, the same tier <see cref="NotInNullableSubqueryFinding"/>/<see cref="TempTableExecShapeFinding"/>
/// use for their own provable-failure claims). <see cref="ControlFlowRiskFindingKind.GotoUsage"/> is
/// <see cref="FindingConfidence.High"/> (an unambiguous structural fact) but SARIF Warning, not Error -
/// a maintainability risk, not a provable wrong outcome. <see
/// cref="ControlFlowRiskFindingKind.CaseExpressionMissingElse"/> and <see
/// cref="ControlFlowRiskFindingKind.NonDeterministicCaseInput"/> are both <see
/// cref="FindingConfidence.High"/>/SARIF Error - both are oracle-confirmed, provably-wrong-outcome
/// facts (a silent NULL fallthrough; a CASE whose WHEN branches are provably almost-always
/// unreachable), the same tier as the other two hard-fact kinds above.
///
/// Version-insensitive: cursor FETCH binding, TRY/CATCH semantics, trigger result-set behavior,
/// isolation-level dirty reads, <c>@@IDENTITY</c>'s session-wide scope, <c>GOTO</c> semantics, and
/// simple-CASE NULL-fallthrough/input-re-evaluation are all long-standing, documented T-SQL mechanics
/// unaffected by compat level or CE mode.
/// </summary>
public sealed record ControlFlowRiskFinding(
    ControlFlowRiskFindingKind Kind,
    string ModuleQualifiedName,
    [property: JsonIgnore] string SourcePath,
    [property: JsonIgnore] int Line,
    [property: JsonIgnore] int Column,
    string DetailText,
    FindingConfidence Confidence = FindingConfidence.Medium)
{
    public SourceSpan Location => new(SourcePath, Line, Column);
}

