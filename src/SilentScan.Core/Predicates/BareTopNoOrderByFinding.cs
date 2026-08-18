using System.Text.Json.Serialization;

namespace SilentScan.Core.Predicates;

/// <summary>
/// docs/detection-checklist.md "Second full-archive practitioner sweep" §G: "Bare <c>TOP (n)</c>
/// with no <c>ORDER BY</c> anywhere in the query" - the row set TOP returns with no ORDER BY is
/// not guaranteed deterministic (parallelism/plan-choice/statistics changes can change which rows
/// come back run to run), so code relying on "the first N rows" without an explicit order is
/// trusting undefined behavior. A correctness claim, not a performance one.
///
/// <b>The claim rests on Microsoft's own documented absence of a determinism guarantee, not on
/// reproducing nondeterminism on demand.</b> Attempted directly against the standing Docker
/// instance anyway, on brand: the same <c>SELECT TOP (5) * FROM t</c> re-run repeatedly against a
/// small, unchanged table returned the identical row set and order every time in this environment
/// (a small heap, one thread, no statistics drift between runs) - a real, honest negative result,
/// not evidence the underlying risk is false. SQL Server's own <c>TOP</c> documentation is explicit
/// that without <c>ORDER BY</c> the rows returned are unordered and the choice of which rows
/// satisfy the clause is itself undefined - this finding reports that documented absence of a
/// guarantee, never a claim that this pass observed nondeterminism itself. Version-insensitive:
/// this is an ANSI/T-SQL semantic guarantee deliberately never made, unaffected by compatibility
/// level or CE mode.
///
/// <b>AST-decidable, no catalog needed at all</b>: a <see
/// cref="Microsoft.SqlServer.TransactSql.ScriptDom.QuerySpecification"/> whose own <c>TopRowFilter</c>
/// is present and whose own <c>OrderByClause</c> is null - both live on the identical query
/// specification node, so "the same query" is exactly "this one query specification", matching the
/// checklist's own AST-decidability note. <c>TOP ... WITH TIES</c> always requires an <c>ORDER BY</c>
/// at the grammar/compile level (Msg 1082 otherwise) and is therefore structurally unreachable here.
///
/// <b>Deliberately excludes <c>TOP (100) PERCENT</c></b> (with or without an ORDER BY, though the
/// ORDER BY case never reaches this scanner anyway since it requires OrderByClause to be null):
/// 100 percent of a result set is every row regardless of TOP's own row-selection nondeterminism -
/// the ROW SET is not actually at risk, only its order (an already-shipped, narrower, view/inline-
/// TVF-scoped claim - <see cref="ViewOrderingFindingKind.TopPercentOrderByNeverLimits"/> - which
/// this finding is deliberately kept distinct from per the checklist's own note; the two streams
/// never overlap since that one requires an ORDER BY to be present and this one requires it to be
/// absent). Every other percent value (1-99) genuinely narrows the row set to an arbitrary,
/// unrepeatable subset and stays in scope.
///
/// Unlike <see cref="ViewOrderingFinding"/>, this scanner is NOT limited to a view/inline TVF's own
/// outermost query - the checklist item states no such restriction, and the nondeterminism risk is
/// identical for a TOP inside a stored procedure's ad-hoc SELECT, a derived table, or any other
/// query position. <see cref="FindingConfidence.Medium"/>: the mechanism itself is a certain,
/// documented engine fact, but whether any real caller actually depends on the returned row SET
/// (rather than, say, using the TOP purely as a sampling/existence-probe convenience where any N
/// rows would do) is workload intent this pass cannot see - the same honest-uncertainty tier <see
/// cref="ViewOrderingFindingKind.OrderByNotGuaranteedToConsumer"/> uses for the sibling "unguaranteed
/// order" claim, one step up from Low since a returned row SET (not merely its order) silently
/// varying is a sharper risk than order alone.
/// </summary>
public sealed record BareTopNoOrderByFinding(
    [property: JsonIgnore] string SourcePath,
    [property: JsonIgnore] int Line,
    [property: JsonIgnore] int Column,
    FindingConfidence Confidence = FindingConfidence.Medium)
{
    public SourceSpan Location => new(SourcePath, Line, Column);
}

