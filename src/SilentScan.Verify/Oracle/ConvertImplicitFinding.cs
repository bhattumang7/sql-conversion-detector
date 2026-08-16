namespace SilentScan.Verify.Oracle;

/// <summary>
/// One CONVERT_IMPLICIT applied to a column reference, found in a plan XML.
/// <paramref name="RangeSeekBound"/> is read per-node, scoped to the operator that owns this
/// specific conversion, not plan-wide - a cached plan for real application SQL can carry several
/// independent conversions at once, and only the ones whose own RelOp actually binds the column
/// through <c>GetRangeThroughConvert</c>'s SeekPredicates are genuinely range-seeking; a sibling
/// conversion elsewhere in the same plan that scans is not "rescued" just because some other
/// predicate in the plan happened to range-seek.
/// </summary>
public sealed record ConvertImplicitFinding(
    string? Database,
    string? Schema,
    string? Table,
    string? Column,
    string ConvertedToDataType,
    bool RangeSeekBound);
