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
}

/// <summary>
/// docs/detection-checklist.md Tier 4 "Cursor and control-flow correctness" - the six members built
/// here. "An output parameter never assigned" (the bullet's own seventh member) is already fully
/// shipped as <see cref="OutputParameterFinding"/> (a path-sensitive "assigned on every return path"
/// analysis, a strict superset of the simpler "never assigned at all" case this Tier 4 entry names) -
/// not rebuilt here, cross-referenced in the checklist instead.
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
/// use for their own provable-failure claims).
///
/// Version-insensitive: cursor FETCH binding, TRY/CATCH semantics, trigger result-set behavior,
/// isolation-level dirty reads, and <c>@@IDENTITY</c>'s session-wide scope are all long-standing,
/// documented T-SQL mechanics unaffected by compat level or CE mode.
/// </summary>
public sealed record ControlFlowRiskFinding(
    ControlFlowRiskFindingKind Kind,
    string ModuleQualifiedName,
    string SourcePath,
    int Line,
    int Column,
    string DetailText,
    FindingConfidence Confidence = FindingConfidence.Medium);
