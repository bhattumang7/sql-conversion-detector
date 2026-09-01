using SilentScan.Core.Catalog;
using SilentScan.Core.TypeInference;

namespace SilentScan.Core.Rules;

public static class VerdictClassifier
{
    public static Verdict Classify(SqlType? columnType, SqlType? otherType, bool otherIsLiteral = false, string? operatorText = null) =>
        ClassifyWithReason(columnType, otherType, otherIsLiteral, operatorText).Verdict;

    public static (Verdict Verdict, string? UnknownReason) ClassifyWithReason(SqlType? columnType, SqlType? otherType, bool otherIsLiteral = false, string? operatorText = null)
    {
        if (columnType is null || otherType is null)
        {
            return (Verdict.Unknown, "operand-type-unresolved");
        }

        if (columnType.Category == SqlTypeCategory.SqlVariant && !IsOutOfModelCategory(otherType.Category))
        {
            return (Verdict.SeekPreserved, null);
        }

        if (otherType.Category == SqlTypeCategory.SqlVariant && !IsOutOfModelCategory(columnType.Category))
        {
            return (Verdict.ScanForced, null);
        }

        if (IsOutOfModelCategory(columnType.Category))
        {
            return (Verdict.Unknown, $"out-of-model-category:{columnType.Category}");
        }

        if (IsOutOfModelCategory(otherType.Category))
        {
            return (Verdict.Unknown, $"out-of-model-category:{otherType.Category}");
        }

        if (!otherIsLiteral && HasGenuineCollationMismatch(columnType, otherType))
        {
            return (Verdict.OperandClash, null);
        }

        if (columnType.Category == otherType.Category)
        {
            return ClassifySameCategory(columnType, otherType);
        }

        return ClassifyCrossCategory(columnType, otherType, otherIsLiteral, operatorText);
    }

    public static bool HasGenuineCollationMismatch(SqlType? columnType, SqlType? otherType) =>
        columnType is { IsStringFamily: true } && otherType is { IsStringFamily: true }
        && columnType.Collation is { } columnCollation && otherType.Collation is { } otherCollation
        && !string.Equals(columnCollation.Name, otherCollation.Name, StringComparison.OrdinalIgnoreCase);

    private static (Verdict Verdict, string? UnknownReason) ClassifyCrossCategory(SqlType columnType, SqlType otherType, bool otherIsLiteral, string? operatorText)
    {

        if (IsLengthTriggeredUnicodePromotion(columnType, otherType))
        {
            return (Verdict.ScanForced, null);
        }

        var outcome = columnType.IsStringFamily
            ? TypePairMatrix.Instance.TryGetOutcomeForColumnCollation(columnType.Category, otherType.Category, columnType.Collation)
            : TypePairMatrix.Instance.TryGetOutcome(columnType.Category, otherType.Category, collationName: null);

        if (outcome is null)
        {
            return (Verdict.Unknown, "no-probed-matrix-cell");
        }

        if (outcome.CompileFailed)
        {

            return (Verdict.OperandClash, null);
        }

        if (!outcome.ColumnConverts)
        {
            return (Verdict.SeekPreserved, null);
        }

        if (!outcome.DynamicRangeSeekAvailable)
        {
            return (Verdict.ScanForced, null);
        }

        var isNonLiteralLike = string.Equals(operatorText, "LIKE", StringComparison.Ordinal) && !otherIsLiteral;
        return (isNonLiteralLike ? Verdict.ScanForced : Verdict.RangeSeek, null);
    }

    private static bool IsLengthTriggeredUnicodePromotion(SqlType columnType, SqlType otherType) =>
        columnType.IsNonUnicodeString && !columnType.IsMax && columnType.Length is > 4000
        && otherType.IsUnicodeString && otherType.IsMax;

    private static bool IsOutOfModelCategory(SqlTypeCategory category) =>
        category is SqlTypeCategory.SqlVariant or SqlTypeCategory.Xml or SqlTypeCategory.UserDefined
            or SqlTypeCategory.Text or SqlTypeCategory.NText or SqlTypeCategory.Image or SqlTypeCategory.Json
            or SqlTypeCategory.Vector;

    private static (Verdict Verdict, string? UnknownReason) ClassifySameCategory(SqlType columnType, SqlType otherType)
    {
        if (!columnType.IsStringFamily)
        {

            return (Verdict.SeekPreserved, null);
        }

        if (columnType.IsMax != otherType.IsMax)
        {
            return (Verdict.RangeSeek, null);
        }

        if (columnType.Collation is null)
        {

            return (Verdict.Unknown, "collation-unresolved");
        }

        if (otherType.Collation is null)
        {

            return (Verdict.SeekPreserved, null);
        }

        if (string.Equals(columnType.Collation.Name, otherType.Collation.Name, StringComparison.OrdinalIgnoreCase))
        {
            return (Verdict.SeekPreserved, null);
        }

        return (Verdict.ScanForced, null);
    }
}
