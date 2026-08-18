using System.Text.Json.Serialization;

namespace SilentScan.Core.Predicates;

public enum ViewOrderingFindingKind
{
    /// <summary>A view/inline TVF's own outermost query uses <c>TOP (100) PERCENT ... ORDER BY</c> - oracle-confirmed provably meaningless: <c>TOP (100) PERCENT</c> never excludes a single row (100% is everything), so unlike a real <c>TOP (N)</c> it cannot even be defended as "the ORDER BY at least decided which rows survived" - its sole purpose is satisfying T-SQL's own "ORDER BY needs TOP/OFFSET/FOR XML in a view" grammar rule, and the resulting order is not guaranteed to any consumer that doesn't apply its own ORDER BY.</summary>
    TopPercentOrderByNeverLimits,

    /// <summary>A view/inline TVF's own outermost query uses a real row-limiting <c>TOP (N)</c> (N &lt; 100 or a non-percent literal) or <c>OFFSET ... FETCH</c> together with <c>ORDER BY</c> - the ORDER BY genuinely does decide which rows survive here, so this is a legitimate use, but the FINAL output order of those surviving rows is still not guaranteed to a consumer that doesn't apply its own ORDER BY. A real, documented Microsoft caveat, weaker and more easily misread as "working" than <see cref="TopPercentOrderByNeverLimits"/> (oracle-observed to often appear to preserve order by coincidence of the chosen plan shape), so reported at lower confidence.</summary>
    OrderByNotGuaranteedToConsumer,
}

/// <summary>
/// docs/detection-checklist.md "Small precise adds": "TOP(100) PERCENT ignored by the optimizer"
/// and "ORDER BY in a view / inline TVF" - shipped together as one finding type, because a direct
/// oracle check (Docker instance) confirmed T-SQL structurally cannot separate them: a bare
/// <c>ORDER BY</c> with no <c>TOP</c>/<c>OFFSET</c>/<c>FOR XML</c> in a view/inline TVF is a hard
/// compile error (Msg 1033), so "ORDER BY in a view" only ever occurs already paired with one of
/// those - the two checklist items describe the same shape from two angles, not two independent
/// ones.
///
/// <b>Oracle-confirmed directly</b> (a real seeded table, a real view with <c>TOP (100) PERCENT ...
/// ORDER BY Amt DESC</c>): querying the view via <c>SELECT TOP 5 * FROM theView</c> with no outer
/// ORDER BY returned rows in the TABLE's own storage order, not the view's own <c>ORDER BY Amt
/// DESC</c> - the view's internal ordering was silently discarded entirely, exactly as this
/// finding's <see cref="ViewOrderingFindingKind.TopPercentOrderByNeverLimits"/> claims. The
/// <see cref="ViewOrderingFindingKind.OrderByNotGuaranteedToConsumer"/> case was also oracle-probed
/// (a view with a genuine <c>TOP (10) ... ORDER BY</c>) and found to sometimes still appear ordered
/// to the consumer purely as a side effect of the chosen plan shape (SQL Server often reuses the
/// same sort it needed internally to compute the TOP) - a real, undocumented, plan-dependent
/// coincidence, not a guarantee, which is exactly why this is the *weaker*, lower-confidence half of
/// this finding rather than a second instance of provable meaninglessness.
///
/// Structural/maintenance risk, not a proven-wrong-result claim (the query never returns a WRONG
/// row set, only a row ORDER a consumer might silently rely on) - SARIF Warning for
/// <see cref="ViewOrderingFindingKind.TopPercentOrderByNeverLimits"/> (<see
/// cref="FindingConfidence.High"/>, the meaninglessness is provable), SARIF Note for
/// <see cref="ViewOrderingFindingKind.OrderByNotGuaranteedToConsumer"/> (<see
/// cref="FindingConfidence.Low"/>, purely informational - this pass cannot see whether any real
/// consumer actually relies on the unguaranteed order, mirroring <see
/// cref="LocalVariablePredicateFinding"/>'s own no-magnitude-claim tier). Version-insensitive: both
/// are ANSI/T-SQL semantic guarantees (rather, the deliberate ABSENCE of one), unaffected by compat
/// level or CE mode.
///
/// <b>Known v1 scope limit, deliberate:</b> only a view's or inline TVF's own OUTERMOST query is
/// inspected, matching the checklist's own explicit "view / inline TVF" scope - a derived
/// table/subquery/CTE using the identical `TOP (100) PERCENT ... ORDER BY` trick (the same Msg 1033
/// grammar rule applies to those too) is a real, structurally identical relative left unanalyzed
/// here rather than silently widened past what was asked for; a multi-statement TVF's own
/// `RETURNS @t TABLE(...)` body has no single outermost query to inspect the same way, so it is
/// never a candidate.
/// </summary>
public sealed record ViewOrderingFinding(
    ViewOrderingFindingKind Kind,
    string ObjectQualifiedName,
    [property: JsonIgnore] string SourcePath,
    [property: JsonIgnore] int Line,
    [property: JsonIgnore] int Column,
    FindingConfidence Confidence)
{
    public SourceSpan Location => new(SourcePath, Line, Column);
}

