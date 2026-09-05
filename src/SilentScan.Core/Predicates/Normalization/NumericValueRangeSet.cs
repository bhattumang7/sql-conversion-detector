namespace SilentScan.Core.Predicates.Normalization;

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

    public static NumericValueRangeSet Universal { get; } = new([new Range(null, true, null, true)], nullPossible: true);

    public bool IsEmpty => _ranges.Count == 0 && !NullPossible;

    public bool HasFullCoverage => _ranges is [{ Lower: null, Upper: null }];

    public static NumericValueRangeSet ForEquals(decimal value) => new([new Range(value, true, value, true)], nullPossible: false);

    public static NumericValueRangeSet ForNotEquals(decimal value) =>
        new([new Range(null, true, value, false), new Range(value, false, null, true)], nullPossible: false);

    public static NumericValueRangeSet ForLessThan(decimal value) => new([new Range(null, true, value, false)], nullPossible: false);

    public static NumericValueRangeSet ForLessThanOrEqual(decimal value) => new([new Range(null, true, value, true)], nullPossible: false);

    public static NumericValueRangeSet ForGreaterThan(decimal value) => new([new Range(value, false, null, true)], nullPossible: false);

    public static NumericValueRangeSet ForGreaterThanOrEqual(decimal value) => new([new Range(value, true, null, true)], nullPossible: false);

    public static NumericValueRangeSet ForIsNull() => new([], nullPossible: true);

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

    public bool IsSubsetOf(NumericValueRangeSet other)
    {
        if (NullPossible && !other.NullPossible)
        {
            return false;
        }

        return _ranges.All(range => other._ranges.Any(candidate => Covers(candidate, range)));
    }

    private static bool Covers(Range outer, Range inner)
    {
        var lowerOk = outer.Lower is null
            || (inner.Lower is { } innerLower
                && (innerLower > outer.Lower.Value || (innerLower == outer.Lower.Value && (outer.LowerInclusive || !inner.LowerInclusive))));

        var upperOk = outer.Upper is null
            || (inner.Upper is { } innerUpper
                && (innerUpper < outer.Upper.Value || (innerUpper == outer.Upper.Value && (outer.UpperInclusive || !inner.UpperInclusive))));

        return lowerOk && upperOk;
    }

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
