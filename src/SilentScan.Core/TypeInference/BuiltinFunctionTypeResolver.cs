namespace SilentScan.Core.TypeInference;

public static class BuiltinFunctionTypeResolver
{
    private static readonly Dictionary<string, SqlType> FixedReturnTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        ["GETDATE"] = new SqlType(SqlTypeCategory.DateTime),
        ["GETUTCDATE"] = new SqlType(SqlTypeCategory.DateTime),
        ["SYSDATETIME"] = new SqlType(SqlTypeCategory.DateTime2, Precision: 7),
        ["SYSUTCDATETIME"] = new SqlType(SqlTypeCategory.DateTime2, Precision: 7),
        ["SYSDATETIMEOFFSET"] = new SqlType(SqlTypeCategory.DateTimeOffset, Precision: 7),
        ["NEWID"] = new SqlType(SqlTypeCategory.UniqueIdentifier),
        ["NEWSEQUENTIALID"] = new SqlType(SqlTypeCategory.UniqueIdentifier),
        ["TEXTPTR"] = new SqlType(SqlTypeCategory.VarBinary, Length: 16),

        ["LEN"] = new SqlType(SqlTypeCategory.Int),
        ["DATALENGTH"] = new SqlType(SqlTypeCategory.Int),
        ["ASCII"] = new SqlType(SqlTypeCategory.Int),
        ["CHARINDEX"] = new SqlType(SqlTypeCategory.Int),
        ["PATINDEX"] = new SqlType(SqlTypeCategory.Int),
        ["ISNUMERIC"] = new SqlType(SqlTypeCategory.Int),
        ["ISDATE"] = new SqlType(SqlTypeCategory.Int),

        ["DATEDIFF"] = new SqlType(SqlTypeCategory.Int),
        ["DATEDIFF_BIG"] = new SqlType(SqlTypeCategory.BigInt),
        ["DATEPART"] = new SqlType(SqlTypeCategory.Int),
        ["DAY"] = new SqlType(SqlTypeCategory.Int),
        ["MONTH"] = new SqlType(SqlTypeCategory.Int),
        ["YEAR"] = new SqlType(SqlTypeCategory.Int),

        ["OBJECT_ID"] = new SqlType(SqlTypeCategory.Int),
        ["OBJECTPROPERTY"] = new SqlType(SqlTypeCategory.Int),

        ["ROW_NUMBER"] = new SqlType(SqlTypeCategory.BigInt),
        ["RANK"] = new SqlType(SqlTypeCategory.BigInt),
        ["DENSE_RANK"] = new SqlType(SqlTypeCategory.BigInt),
        ["NTILE"] = new SqlType(SqlTypeCategory.BigInt),
        ["COUNT"] = new SqlType(SqlTypeCategory.Int),
        ["COUNT_BIG"] = new SqlType(SqlTypeCategory.BigInt),

        ["SUSER_SNAME"] = new SqlType(SqlTypeCategory.NVarChar, Length: 128),
        ["SUSER_NAME"] = new SqlType(SqlTypeCategory.NVarChar, Length: 128),
        ["USER_NAME"] = new SqlType(SqlTypeCategory.NVarChar, Length: 128),
        ["APP_NAME"] = new SqlType(SqlTypeCategory.NVarChar, Length: 128),
        ["DB_NAME"] = new SqlType(SqlTypeCategory.NVarChar, Length: 128),
        ["HOST_NAME"] = new SqlType(SqlTypeCategory.NVarChar, Length: 128),
        ["ORIGINAL_LOGIN"] = new SqlType(SqlTypeCategory.NVarChar, Length: 4000),

        ["UNICODE"] = new SqlType(SqlTypeCategory.Int),
        ["CHAR"] = new SqlType(SqlTypeCategory.Char, Length: 1),
        ["NCHAR"] = new SqlType(SqlTypeCategory.NChar, Length: 1),
        ["SPACE"] = new SqlType(SqlTypeCategory.VarChar, LengthKnown: false),
        ["QUOTENAME"] = new SqlType(SqlTypeCategory.NVarChar, Length: 258),
        ["SOUNDEX"] = new SqlType(SqlTypeCategory.VarChar, Length: 5),
        ["DIFFERENCE"] = new SqlType(SqlTypeCategory.Int),
        ["ISJSON"] = new SqlType(SqlTypeCategory.Int),
    };

    private static readonly Dictionary<string, SqlType> GlobalVariableTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        ["@@SPID"] = new SqlType(SqlTypeCategory.SmallInt),
        ["@@ROWCOUNT"] = new SqlType(SqlTypeCategory.Int),
        ["@@ERROR"] = new SqlType(SqlTypeCategory.Int),
        ["@@TRANCOUNT"] = new SqlType(SqlTypeCategory.Int),
        ["@@NESTLEVEL"] = new SqlType(SqlTypeCategory.Int),
        ["@@FETCH_STATUS"] = new SqlType(SqlTypeCategory.Int),

        ["@@CURSOR_ROWS"] = new SqlType(SqlTypeCategory.Int),
        ["@@MAX_CONNECTIONS"] = new SqlType(SqlTypeCategory.Int),
        ["@@LANGID"] = new SqlType(SqlTypeCategory.SmallInt),
        ["@@PACK_RECEIVED"] = new SqlType(SqlTypeCategory.Int),
        ["@@CPU_BUSY"] = new SqlType(SqlTypeCategory.Int),
    };

    private static readonly Dictionary<string, int> ArgumentTypeFunctions = new(StringComparer.OrdinalIgnoreCase)
    {
        ["ISNULL"] = 0,
        ["UPPER"] = 0,
        ["LOWER"] = 0,
        ["LTRIM"] = 0,
        ["RTRIM"] = 0,
        ["REVERSE"] = 0,
        ["REPLACE"] = 0,
        ["LEFT"] = 0,
        ["RIGHT"] = 0,
        ["SUBSTRING"] = 0,
        ["STUFF"] = 0,
        ["MIN"] = 0,
        ["MAX"] = 0,
        ["SUM"] = 0,
        ["AVG"] = 0,
        ["DATEADD"] = 2,
        ["TRIM"] = 0,
        ["TRANSLATE"] = 0,
    };

    private static readonly HashSet<string> IntegerWideningAggregates = new(StringComparer.OrdinalIgnoreCase) { "SUM", "AVG" };

    private static readonly HashSet<string> LengthUnknownAfterArgumentType = new(StringComparer.OrdinalIgnoreCase)
    {
        "LEFT", "RIGHT", "SUBSTRING", "STUFF", "REPLACE", "TRANSLATE",
    };

    public static bool ResultLengthDiffersFromArgument(string functionName) =>
        LengthUnknownAfterArgumentType.Contains(functionName);

    public static SqlType ClearLengthIfUnknown(SqlType argumentType) =>
        argumentType with { Length = null, LengthKnown = false };

    private static readonly HashSet<string> DemotesFixedWidthSourceCategory = new(StringComparer.OrdinalIgnoreCase)
    {
        "UPPER", "LOWER", "LTRIM", "RTRIM", "REVERSE", "REPLACE", "LEFT", "RIGHT", "SUBSTRING", "STUFF", "TRIM", "TRANSLATE",
    };

    public static bool DemotesFixedWidthArgumentCategory(string functionName) =>
        DemotesFixedWidthSourceCategory.Contains(functionName);

    public static SqlType DemoteFixedWidthCategory(SqlType argumentType) => argumentType.Category switch
    {
        SqlTypeCategory.Char => argumentType with { Category = SqlTypeCategory.VarChar },
        SqlTypeCategory.NChar => argumentType with { Category = SqlTypeCategory.NVarChar },
        SqlTypeCategory.Binary => argumentType with { Category = SqlTypeCategory.VarBinary },
        _ => argumentType,
    };

    public static bool RequiresDateAddResultAdjustment(string functionName) =>
        string.Equals(functionName, "DATEADD", StringComparison.OrdinalIgnoreCase);

    public static SqlType? AdjustArgumentTypeFunctionResult(string functionName, SqlType? argumentType)
    {
        if (argumentType is null)
        {
            return null;
        }

        if (WidensIntegerAggregateArgument(functionName))
        {
            var widened = WidenIntegerAggregateResult(argumentType);
            return IsAverageAggregate(functionName) && widened is { Category: SqlTypeCategory.Decimal, Scale: { } avgScale }
                ? widened with { Scale = Math.Max(avgScale, 6) }
                : widened;
        }

        if (RequiresDateAddResultAdjustment(functionName))
        {
            return ResolveDateAddResult(argumentType);
        }

        if (DemotesFixedWidthArgumentCategory(functionName))
        {
            argumentType = DemoteFixedWidthCategory(argumentType);
        }

        if (ResultLengthDiffersFromArgument(functionName))
        {
            argumentType = ClearLengthIfUnknown(argumentType);
        }

        return argumentType;
    }

    public static SqlType ResolveDateAddResult(SqlType thirdArgumentType) =>
        thirdArgumentType.IsDateTimeFamily
            ? thirdArgumentType
            : new SqlType(SqlTypeCategory.DateTime);

    public static SqlType? ResolveFixedReturnType(string functionName) =>
        FixedReturnTypes.GetValueOrDefault(functionName);

    public static int? TryGetArgumentTypeIndex(string functionName) =>
        ArgumentTypeFunctions.TryGetValue(functionName, out var index) ? index : null;

    public static bool WidensIntegerAggregateArgument(string functionName) =>
        IntegerWideningAggregates.Contains(functionName);

    public static SqlType WidenIntegerAggregateResult(SqlType argumentType) => argumentType.Category switch
    {
        SqlTypeCategory.TinyInt or SqlTypeCategory.SmallInt => new SqlType(SqlTypeCategory.Int),
        SqlTypeCategory.Decimal => argumentType with { Precision = 38 },
        _ => argumentType,
    };

    private static bool IsAverageAggregate(string functionName) =>
        string.Equals(functionName, "AVG", StringComparison.OrdinalIgnoreCase);

    public static SqlType? ResolveGlobalVariable(string name) =>
        GlobalVariableTypes.GetValueOrDefault(name);

    public static SqlType? ResolveStringAggResult(SqlType? valueType)
    {
        if (valueType is not { IsStringFamily: true })
        {
            return null;
        }

        var category = DemoteFixedWidthCategory(valueType).Category;
        if (valueType.IsMax)
        {
            return new SqlType(category, IsMax: true, Collation: valueType.Collation);
        }

        var cap = category is SqlTypeCategory.NChar or SqlTypeCategory.NVarChar ? UnicodeAggCapChars : NonUnicodeAggCapChars;
        return new SqlType(category, Length: cap, Collation: valueType.Collation);
    }

    private const int NonUnicodeAggCapChars = 8000;
    private const int UnicodeAggCapChars = 4000;
}
