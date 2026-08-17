namespace SilentScan.Core.Predicates;

public enum OutputParameterFindingKind
{
    /// <summary>
    /// A <c>CREATE/ALTER PROCEDURE</c> parameter declared <c>OUTPUT</c> reaches a
    /// <c>RETURN</c> statement, or the natural end of the module body, on some statically
    /// reachable path with no intervening assignment (<c>SET @p = ...</c>,
    /// <c>SELECT @p = ...</c>, or passing <c>@p</c> onward as an <c>OUTPUT</c> argument to
    /// another procedure call) at the same scope. A path-sensitive strengthening of "never
    /// assigned at all": a parameter assigned on SOME paths but not others still fires, since
    /// the defect is per-path, not per-procedure.
    /// </summary>
    UnassignedOnSomePath,
}

/// <summary>
/// docs/detection-checklist.md "Second OSS/commercial sweep": "Output parameter not populated on
/// every code path" (`ErikEJ` fork, `SR0013`). Shipped as its own standalone, complete,
/// path-sensitive rule here - NOT folded into the Tier 4 "output parameter never assigned" entry
/// the checklist item's own text names, because Tier 4 is out of scope for this whole pass of
/// work and was deliberately left untouched. A correct path-sensitive analysis naturally subsumes
/// the simpler "never assigned on any path" case as one end of the same spectrum, so nothing from
/// the Tier 4 framing is lost by shipping it here instead.
///
/// <b>Oracle-confirmed the real caller-visible risk directly</b> (real execution on the Docker
/// instance, a procedure whose OUTPUT parameter is genuinely never assigned on the path taken):
/// the calling session's own variable is left completely UNCHANGED by the call - not reset to
/// NULL, not defaulted, literally untouched. A caller variable that started at a real prior value
/// (e.g. `999`) stays `999`; one that started `NULL` stays `NULL`. This is a SHARPER and more
/// dangerous claim than "the caller gets NULL": a caller reusing the same local variable across
/// several calls (a common pattern for an accumulator/status-code parameter) can silently read
/// STALE data from a previous, unrelated call and never notice, since nothing about the value
/// itself signals it wasn't just written.
///
/// Reuses the exact same reachability-walk discipline <see cref="TransactionHygieneScanner"/>
/// already established for "does every path resolve a state", adapted from tracking one open
/// transaction site to tracking a SET of not-yet-guaranteed-assigned output parameter names -
/// see <see cref="OutputParameterScanner"/>'s own doc comment for the full analysis and its known
/// v1 scope limits.
///
/// A correctness finding, not a plan-shape one - no plan-XML oracle applies.
/// <see cref="FindingConfidence.High"/>, SARIF Warning - the same "structural risk, not a
/// plan-shape claim" tier <see cref="TransactionHygieneFinding"/>/<see cref="ForcedSerialFinding"/>
/// already use (not Error: unlike e.g. <see cref="NotInNullableSubqueryFinding"/>, this pass
/// cannot see whether a real caller ever reads the parameter's post-call value at all, so the
/// magnitude of harm is genuinely conditional on caller behavior this tool cannot observe).
/// Version-insensitive: OUTPUT parameter marshalling is ANSI/T-SQL calling-convention semantics,
/// unaffected by compat level or CE mode.
/// </summary>
public sealed record OutputParameterFinding(
    OutputParameterFindingKind Kind,
    string SourcePath,
    string ParameterName,
    int ProcedureLine,
    int ProcedureColumn,
    int UnresolvedExitLine,
    int UnresolvedExitColumn,
    FindingConfidence Confidence = FindingConfidence.High);
