namespace SilentScan.Core.Predicates.Normalization;

/// <summary>
/// The set of numeric values a column could still hold given every literal comparison seen so far
/// - a sorted list of disjoint intervals over the non-null domain, plus a separate NULL-
/// admissibility flag. This is the same shape the real query optimizer keeps per column while it
/// folds a predicate: build the set from one comparison, then <see cref="Intersect"/> it against
/// every AND-sibling's own set (a comparison is never true for a NULL value, so intersecting any
/// ordinary comparison always drops <see cref="NullPossible"/> - only IS NULL re-admits it) or
/// <see cref="Union"/> it against every OR-sibling's own set. <see cref="IsEmpty"/> after
/// intersecting a whole conjunction is exactly "this AND can never be satisfied" - a real
/// contradiction, derived from the values involved, not a text-pattern guess. <see cref="HasFullCoverage"/>
/// after unioning a whole disjunction is exactly "every non-null value satisfies at least one
/// disjunct" - the numeric half of a real tautology (the caller still has to reason about NULL
/// separately, since that depends on the column's own nullability, not on this set).
/// </summary>
internal sealed class NumericValueRangeSet
{
    private readonly record struct Range(decimal? Lower, bool LowerInclusive, decimal? Upper, bool UpperInclusive);

    private readonly IReadOnlyList<Range> _ranges;

    public bool NullPossible { get; }

    private NumericValueRangeSet(IReadOnlyList<Range> ranges, bool nullPossible)
    {
        _ranges = ranges;
        NullPossible = nullPossible;
    }

    /// <summary>No constraint applied yet: every non-null value is still possible, and so is NULL.</summary>
    public static NumericValueRangeSet Universal { get; } = new([new Range(null, true, null, true)], nullPossible: true);

    public bool IsEmpty => _ranges.Count == 0 && !NullPossible;

    /// <summary>Every non-null value is covered by at least one range - the numeric half of a tautology.</summary>
    public bool HasFullCoverage => _ranges is [{ Lower: null, Upper: null }];

    public static NumericValueRangeSet ForEquals(decimal value) => new([new Range(value, true, value, true)], nullPossible: false);

    public static NumericValueRangeSet ForNotEquals(decimal value) =>
        new([new Range(null, true, value, false), new Range(value, false, null, true)], nullPossible: false);

    public static NumericValueRangeSet ForLessThan(decimal value) => new([new Range(null, true, value, false)], nullPossible: false);

    public static NumericValueRangeSet ForLessThanOrEqual(decimal value) => new([new Range(null, true, value, true)], nullPossible: false);

    public static NumericValueRangeSet ForGreaterThan(decimal value) => new([new Range(value, false, null, true)], nullPossible: false);

    public static NumericValueRangeSet ForGreaterThanOrEqual(decimal value) => new([new Range(value, true, null, true)], nullPossible: false);

    /// <summary>IS NULL: no non-null value satisfies it, but NULL itself does.</summary>
    public static NumericValueRangeSet ForIsNull() => new([], nullPossible: true);

    /// <summary>IS NOT NULL: every non-null value is still possible, NULL is not.</summary>
    public static NumericValueRangeSet ForIsNotNull() => new([new Range(null, true, null, true)], nullPossible: false);

    public NumericValueRangeSet Intersect(NumericValueRangeSet other)
    {
        var result = new List<Range>();
        foreach (var a in _ranges)
        {
            foreach (var b in other._ranges)
            {
                var (lower, lowerInclusive) = TighterLower(a.Lower, a.LowerInclusive, b.Lower, b.LowerInclusive);
                var (upper, upperInclusive) = TighterUpper(a.Upper, a.UpperInclusive, b.Upper, b.UpperInclusive);
                if (IsNonEmpty(lower, lowerInclusive, upper, upperInclusive))
                {
                    result.Add(new Range(lower, lowerInclusive, upper, upperInclusive));
                }
            }
        }

        return new NumericValueRangeSet(Coalesce(result), NullPossible && other.NullPossible);
    }

    public NumericValueRangeSet Union(NumericValueRangeSet other) =>
        new(Coalesce([.. _ranges, .. other._ranges]), NullPossible || other.NullPossible);

    private static (decimal? Value, bool Inclusive) TighterLower(decimal? a, bool aIncl, decimal? b, bool bIncl)
    {
        if (a is null)
        {
            return (b, bIncl);
        }

        if (b is null)
        {
            return (a, aIncl);
        }

        if (a.Value != b.Value)
        {
            return a.Value > b.Value ? (a, aIncl) : (b, bIncl);
        }

        return (a, aIncl && bIncl);
    }

    private static (decimal? Value, bool Inclusive) TighterUpper(decimal? a, bool aIncl, decimal? b, bool bIncl)
    {
        if (a is null)
        {
            return (b, bIncl);
        }

        if (b is null)
        {
            return (a, aIncl);
        }

        if (a.Value != b.Value)
        {
            return a.Value < b.Value ? (a, aIncl) : (b, bIncl);
        }

        return (a, aIncl && bIncl);
    }

    private static bool IsNonEmpty(decimal? lower, bool lowerInclusive, decimal? upper, bool upperInclusive)
    {
        if (lower is null || upper is null)
        {
            return true;
        }

        if (lower.Value < upper.Value)
        {
            return true;
        }

        return lower.Value == upper.Value && lowerInclusive && upperInclusive;
    }

    private static List<Range> Coalesce(List<Range> ranges)
    {
        if (ranges.Count == 0)
        {
            return [];
        }

        ranges.Sort(CompareLowerBounds);

        var merged = new List<Range> { ranges[0] };
        foreach (var next in ranges.Skip(1))
        {
            var current = merged[^1];

            if (!TryMerge(current, next, out var mergedRange))
            {
                merged.Add(next);
                continue;
            }

            merged[^1] = mergedRange;
        }

        return merged;
    }

    private static int CompareLowerBounds(Range left, Range right)
    {
        if (left.Lower is null)
        {
            return right.Lower is null ? 0 : -1;
        }

        return right.Lower is null ? 1 : left.Lower.Value.CompareTo(right.Lower.Value);
    }

    private static bool TryMerge(Range current, Range next, out Range merged)
    {
        if (current.Upper is not null && next.Lower is not null
            && (next.Lower.Value > current.Upper.Value
                || (next.Lower.Value == current.Upper.Value && !current.UpperInclusive && !next.LowerInclusive)))
        {
            merged = default;
            return false;
        }

        var (upper, upperInclusive) = SelectUpperBound(current, next);
        merged = current with { Upper = upper, UpperInclusive = upperInclusive };
        return true;
    }

    private static (decimal? Upper, bool Inclusive) SelectUpperBound(Range current, Range next)
    {
        if (current.Upper is null)
        {
            return (current.Upper, current.UpperInclusive);
        }

        if (next.Upper is null || next.Upper.Value > current.Upper.Value)
        {
            return (next.Upper, next.UpperInclusive);
        }

        if (next.Upper.Value < current.Upper.Value)
        {
            return (current.Upper, current.UpperInclusive);
        }

        return (current.Upper, current.UpperInclusive || next.UpperInclusive);
    }
}
