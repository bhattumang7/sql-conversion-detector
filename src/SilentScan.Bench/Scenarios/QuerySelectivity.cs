namespace SilentScan.Bench.Scenarios;

/// <summary>
/// How much of the table a benchmark cell's predicate matches (CLAUDE.md Benchmark protocol,
/// extended by an audit finding: the original harness only ever probed a single unique row, so
/// a `RangeSeek` verdict's own machinery - <c>GetRangeThroughConvert</c> - was never actually
/// exercised at a scale where its overhead could show up; a 1-row seek and a 1-row dynamic
/// range seek are both trivially cheap regardless of which one is "supposed to" cost more).
/// </summary>
public enum QuerySelectivity
{
    /// <summary>A single unique row via equality - the original benchmark shape, kept for the point-lookup case.</summary>
    SingleRow,

    /// <summary>A contiguous range covering ~1% of the table via `Code &gt;= @lo AND Code &lt; @hi`.</summary>
    OnePercent,

    /// <summary>A contiguous range covering ~10% of the table via `Code &gt;= @lo AND Code &lt; @hi`.</summary>
    TenPercent,
}

/// <summary>Fraction-of-table helpers for <see cref="QuerySelectivity"/>.</summary>
public static class QuerySelectivityExtensions
{
    public static double? Fraction(this QuerySelectivity selectivity) => selectivity switch
    {
        QuerySelectivity.OnePercent => 0.01,
        QuerySelectivity.TenPercent => 0.10,
        _ => null,
    };
}
