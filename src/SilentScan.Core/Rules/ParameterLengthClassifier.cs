using SilentScan.Core.Catalog;
using SilentScan.Core.TypeInference;

namespace SilentScan.Core.Rules;

public static class ParameterLengthClassifier
{
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

    public static (int ColumnLength, int? OtherLength, bool IsImplicitDefault)? ClassifyUnderLength(SqlType? columnType, SqlType? otherType)
    {
        if (columnType is not { IsStringFamily: true, IsMax: false, Length: { } columnLength }
            || otherType is not { IsStringFamily: true, IsMax: false }
            || columnType.Category != otherType.Category)
        {
            return null;
        }

        var isImplicitDefault = otherType.Length is null;
        if (isImplicitDefault && !otherType.LengthKnown)
        {

            return null;
        }

        if (!isImplicitDefault && otherType.Length >= columnLength)
        {
            return null;
        }

        return (columnLength, otherType.Length, isImplicitDefault);
    }

    public static bool ChangesRangeOrPatternShape(string operatorText) =>
        operatorText is "LIKE" or "<" or "<=" or ">" or ">=";
}
