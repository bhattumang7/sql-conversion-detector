using SilentScan.Core.TypeInference;

namespace SilentScan.Core.Rules;

public static class NumericFamilyNarrowing
{
    public enum Kind
    {
        ScaleNarrowing,
        ApproximateToExactTruncation,
    }

    public readonly record struct Result(Kind Kind, int TargetScale, bool TargetIsExact);

    private enum Family
    {
        Exact,
        Approximate,
    }

    private static readonly Dictionary<SqlTypeCategory, (Family Family, Func<SqlType, int> Rank)> Profiles =
        new Dictionary<SqlTypeCategory, (Family, Func<SqlType, int>)>
        {
            [SqlTypeCategory.TinyInt] = (Family.Exact, _ => 0),
            [SqlTypeCategory.SmallInt] = (Family.Exact, _ => 0),
            [SqlTypeCategory.Int] = (Family.Exact, _ => 0),
            [SqlTypeCategory.BigInt] = (Family.Exact, _ => 0),
            [SqlTypeCategory.SmallMoney] = (Family.Exact, _ => 4),
            [SqlTypeCategory.Money] = (Family.Exact, _ => 4),
            [SqlTypeCategory.Decimal] = (Family.Exact, type => type.Scale ?? 0),
            [SqlTypeCategory.Real] = (Family.Approximate, _ => 24),
            [SqlTypeCategory.Float] = (Family.Approximate, _ => 53),
        };

    private const int DefaultDecimalPrecision = 18;

    public static bool IsDecimalPrecisionNarrowed(SqlType target, SqlType source) =>
        target.Category == SqlTypeCategory.Decimal && source.Category == SqlTypeCategory.Decimal
        && (target.Precision ?? DefaultDecimalPrecision) < (source.Precision ?? DefaultDecimalPrecision);

    public static Result? Classify(SqlType target, SqlType source)
    {
        if (!Profiles.TryGetValue(target.Category, out var targetProfile) || !Profiles.TryGetValue(source.Category, out var sourceProfile))
        {
            return null;
        }

        var targetIsExact = targetProfile.Family == Family.Exact;

        if (targetIsExact && sourceProfile.Family == Family.Approximate)
        {
            return new Result(Kind.ApproximateToExactTruncation, targetProfile.Rank(target), targetIsExact);
        }

        if (targetProfile.Family != sourceProfile.Family)
        {
            return null;
        }

        var targetRank = targetProfile.Rank(target);
        var sourceRank = sourceProfile.Rank(source);
        return targetRank < sourceRank ? new Result(Kind.ScaleNarrowing, targetRank, targetIsExact) : null;
    }
}
