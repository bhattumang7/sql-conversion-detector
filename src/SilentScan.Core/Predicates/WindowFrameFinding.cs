using System.Text.Json.Serialization;

namespace SilentScan.Core.Predicates;

public enum WindowFrameFindingKind
{
    /// <summary>An explicit <c>RANGE</c> frame (<c>RANGE BETWEEN ...</c> / <c>RANGE UNBOUNDED ...</c>) on a window function with an <c>ORDER BY</c> in its <c>OVER</c> clause.</summary>
    ExplicitRangeFrame,

    /// <summary>A window function's <c>OVER</c> clause has an <c>ORDER BY</c> but no explicit frame clause at all - T-SQL silently defaults this to <c>RANGE BETWEEN UNBOUNDED PRECEDING AND CURRENT ROW</c>, the same mechanism as <see cref="ExplicitRangeFrame"/>, just invisible in the source text.</summary>
    ImplicitDefaultRangeFrame,
}

/// <summary>
/// docs/detection-checklist.md "Small precise adds": RANGE instead of ROWS in window-function
/// frames. Syntax-only, no catalog dependency - both <see cref="WindowFrameFindingKind"/> members
/// are visible directly from the AST (an explicit <c>RANGE</c> keyword, or an <c>OVER</c> clause
/// with <c>ORDER BY</c> and no frame clause at all, which T-SQL defaults to <c>RANGE</c>).
///
/// <b>Oracle-corrected from the checklist's own "on-disk spool per partition" framing</b>: probed
/// directly (Docker instance, a 5,000-row seeded table, <c>SET STATISTICS XML ON</c> against real
/// executions) and found BOTH an equivalent <c>ROWS</c> frame and a <c>RANGE</c> frame produce the
/// identical <c>PhysicalOp="Window Spool"</c> operator - there is no on-disk-vs-not distinction
/// between the two frame types at the physical-operator level, so that specific claim does not
/// survive contact with the oracle and is not repeated in this finding's own wording. The real,
/// reproduced differentiator: the <c>Window Spool</c> operator's own <c>ActualCPUms</c> was
/// measured at roughly 4x higher for the <c>RANGE</c> frame than the equivalent <c>ROWS</c> frame
/// across repeated runs against identical data (a real, if modest-scale, execution-time cost from
/// the peer-group value-comparison <c>RANGE</c> requires that <c>ROWS</c>'s pure physical-offset
/// counting does not) - and the implicit-default-frame case was independently confirmed to cost the
/// same as the explicit <c>RANGE</c> case, not the cheaper <c>ROWS</c> case, since it genuinely
/// compiles to the identical mechanism.
///
/// Purely a performance-cost finding, not a correctness one (the result is identical either way) -
/// <see cref="FindingConfidence.High"/> by default but SARIF Warning, the same "structural risk, not
/// provably-wrong-result" tier <see cref="ForcedSerialFinding"/>/<see cref="CatchAllPredicateFinding"/>
/// already use. Version-insensitive: RANGE-vs-ROWS frame cost is a long-standing execution-engine
/// property, unaffected by compat level or CE mode.
///
/// <b>Known v1 scope limit:</b> only fires when an <c>ORDER BY</c> is present in the <c>OVER</c>
/// clause - a frame clause with no <c>ORDER BY</c> at all is a compile error for <c>ROWS</c>/<c>RANGE</c>
/// (T-SQL requires it), so this can never occur in valid SQL and is not a gap.
/// </summary>
public sealed record WindowFrameFinding(
    WindowFrameFindingKind Kind,
    [property: JsonIgnore] string SourcePath,
    [property: JsonIgnore] int Line,
    [property: JsonIgnore] int Column,
    FindingConfidence Confidence = FindingConfidence.High)
{
    public SourceSpan Location => new(SourcePath, Line, Column);
}

