using SilentScan.Core.Catalog;

namespace SilentScan.Core.Rules;

/// <summary>
/// Pure decisions for the oversized/under-length string-parameter findings, extracted out of
/// <c>TypedPredicateExtractor</c>'s visitor (docs/detection-checklist.md "Engineering debt" -
/// separating rule decisions from ScriptDom traversal mechanics). Both take the column's and the
/// other operand's already-resolved <see cref="SqlType"/> - recognizing that the other operand is
/// a real (non-literal) variable/parameter/expression, and resolving its type, stay the caller's
/// own concern.
/// </summary>
public static class ParameterLengthClassifier
{
    /// <summary>
    /// docs/detection-checklist.md Tier 1 "Oversized and MAX-typed parameters" #2 - a
    /// parameter/variable/expression declared with a meaningfully LONGER length than the column
    /// it's compared against, within the same string category (a category MISMATCH is a
    /// different, already-covered concern; MAX-typed is its own item #1, not this one - a
    /// declared length of -1 there would falsely read as "shorter", so MAX-typed operands are
    /// excluded here explicitly). Returns null when the shape doesn't apply or the other operand
    /// isn't actually longer.
    /// </summary>
    public static (int ColumnLength, int OtherLength)? ClassifyOversized(SqlType? columnType, SqlType? otherType)
    {
        if (columnType is not { IsStringFamily: true, IsMax: false, Length: { } columnLength }
            || otherType is not { IsStringFamily: true, IsMax: false, Length: { } otherLength }
            || columnType.Category != otherType.Category
            || otherLength <= columnLength)
        {
            return null;
        }

        return (columnLength, otherLength);
    }

    /// <summary>
    /// docs/detection-checklist.md Tier 1 "Under-length and length-defaulted string
    /// declarations" - the mirror of <see cref="ClassifyOversized"/>: a parameter/variable/
    /// expression declared with a meaningfully SHORTER length than the column it's compared
    /// against (or no explicit length at all, T-SQL's own length-1 default), within the same
    /// string category. Same MAX/category-mismatch exclusions as the oversized case. Returns null
    /// when the shape doesn't apply or the other operand isn't actually shorter.
    /// </summary>
    public static (int ColumnLength, int? OtherLength, bool IsImplicitDefault)? ClassifyUnderLength(SqlType? columnType, SqlType? otherType)
    {
        if (columnType is not { IsStringFamily: true, IsMax: false, Length: { } columnLength }
            || otherType is not { IsStringFamily: true, IsMax: false }
            || columnType.Category != otherType.Category)
        {
            return null;
        }

        var isImplicitDefault = otherType.Length is null;
        if (!isImplicitDefault && otherType.Length >= columnLength)
        {
            return null;
        }

        return (columnLength, otherType.Length, isImplicitDefault);
    }

    /// <summary>
    /// True when <paramref name="operatorText"/> is <c>LIKE</c> or a range comparison - truncating
    /// a LIKE pattern or a range bound doesn't just risk excluding an exact match, it changes what
    /// the whole comparison MEANS (a shorter LIKE pattern matches a broader set of rows; a
    /// truncated range bound moves the boundary).
    /// </summary>
    public static bool ChangesRangeOrPatternShape(string operatorText) =>
        operatorText is "LIKE" or "<" or "<=" or ">" or ">=";
}
