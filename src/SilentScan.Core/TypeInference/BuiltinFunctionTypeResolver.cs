using SilentScan.Core.Catalog;
using SilentScan.Core.TypeInference;

namespace SilentScan.Core.TypeInference;

/// <summary>
/// Return types for a curated set of built-in T-SQL scalar functions and <c>@@</c> global
/// variables, every entry verified directly against <c>sys.dm_exec_describe_first_result_set</c>
/// on the Docker oracle rather than taken from documentation or memory (CLAUDE.md precision
/// discipline: never guess). Before this existed, ANY function call or global variable on the
/// non-column side of a predicate resolved Unknown - the single largest driver of this tool's
/// Unknown-verdict rate in real corpora (an audit finding: ~95% of Unknown verdicts traced back
/// to an untyped operand, overwhelmingly a function call like GETDATE()/LEN()/ISNULL(), not an
/// actually-uncertain type precedence question). A function not in this table still resolves
/// Unknown - this is a curated allowlist, not a general function-signature database, and it stays
/// that way: an entry only belongs here once it's been checked against the real engine.
/// </summary>
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

        // ORIGINAL_LOGIN() is the one outlier at nvarchar(4000), not the nvarchar(128) every
        // other identity function returns - verified directly, not assumed from the family
        // pattern the others share.
        ["SUSER_SNAME"] = new SqlType(SqlTypeCategory.NVarChar, Length: 128),
        ["SUSER_NAME"] = new SqlType(SqlTypeCategory.NVarChar, Length: 128),
        ["USER_NAME"] = new SqlType(SqlTypeCategory.NVarChar, Length: 128),
        ["APP_NAME"] = new SqlType(SqlTypeCategory.NVarChar, Length: 128),
        ["DB_NAME"] = new SqlType(SqlTypeCategory.NVarChar, Length: 128),
        ["HOST_NAME"] = new SqlType(SqlTypeCategory.NVarChar, Length: 128),
        ["ORIGINAL_LOGIN"] = new SqlType(SqlTypeCategory.NVarChar, Length: 4000),
    };

    private static readonly Dictionary<string, SqlType> GlobalVariableTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        ["@@SPID"] = new SqlType(SqlTypeCategory.SmallInt),
        ["@@ROWCOUNT"] = new SqlType(SqlTypeCategory.Int),
        ["@@ERROR"] = new SqlType(SqlTypeCategory.Int),
        ["@@TRANCOUNT"] = new SqlType(SqlTypeCategory.Int),
        ["@@NESTLEVEL"] = new SqlType(SqlTypeCategory.Int),
        ["@@FETCH_STATUS"] = new SqlType(SqlTypeCategory.Int),

        // Oracle-verified via sys.dm_exec_describe_first_result_set, same method as every entry
        // above - added after a corpus/test audit flagged @@CURSOR_ROWS specifically as an
        // "unavoidable" Unknown example that turned out to just be missing from this table.
        ["@@CURSOR_ROWS"] = new SqlType(SqlTypeCategory.Int),
        ["@@MAX_CONNECTIONS"] = new SqlType(SqlTypeCategory.Int),
        ["@@LANGID"] = new SqlType(SqlTypeCategory.SmallInt),
        ["@@PACK_RECEIVED"] = new SqlType(SqlTypeCategory.Int),
        ["@@CPU_BUSY"] = new SqlType(SqlTypeCategory.Int),
    };

    /// <summary>
    /// Function names whose return type is exactly ONE of their arguments' own type, unmodified -
    /// each verified directly against the real oracle. The value is which argument (0-based).
    /// ISNULL(1, @varcharVariable) returns int, not the higher-precedence varchar - ISNULL never
    /// applies data type precedence across its two arguments the way COALESCE does. Distinct from
    /// COALESCE/NULLIF, which CLAUDE.md's hard-cases list calls out as needing their own explicit
    /// precedence-aware rule - this is deliberately not that.
    ///
    /// Roadmap Phase B: the common string-transform builtins (UPPER/LOWER/LTRIM/RTRIM/REVERSE/
    /// REPLACE/LEFT/RIGHT/SUBSTRING/STUFF) verified the same way - each preserves its first
    /// (the source string) argument's own UNICODE-ness and collation exactly (probed:
    /// LOWER(nvarcharCol) returns nvarchar with the source's own collation; UPPER(varcharCol)
    /// returns varchar, collation unchanged), but demotes a fixed-width source category
    /// (char/nchar/binary) to its variable-width counterpart - see
    /// <see cref="DemotesFixedWidthArgumentCategory"/>/<see cref="DemoteFixedWidthCategory"/>.
    /// Length/precision facets are not otherwise computed faithfully (SQL Server computes a real
    /// per-function result length; LEFT/RIGHT/SUBSTRING/STUFF/REPLACE instead clear Length/
    /// LengthKnown via <see cref="ResultLengthDiffersFromArgument"/>/<see cref="ClearLengthIfUnknown"/>
    /// rather than assert the source's own declared length, which is provably wrong for these -
    /// UPPER/LOWER/LTRIM/RTRIM/REVERSE keep the source's declared length exactly, since only the
    /// runtime content, never the declared max length, changes for those). CONCAT is deliberately
    /// NOT included - its return type depends on ALL arguments (nvarchar if any is unicode,
    /// varchar otherwise), a genuinely different, multi-argument rule this single-argument
    /// mechanism can't express; left as a documented gap rather than force-fit.
    ///
    /// MIN/MAX preserve their (only) argument's exact type unmodified - oracle-verified
    /// (MIN(TinyIntCol) returns tinyint, MAX(MoneyCol) returns money; unlike SUM/AVG below,
    /// neither widens an integer-family argument). DATEADD's return type follows its THIRD
    /// argument (the date expression, index 2, not its datepart keyword at index 0) ONLY when
    /// that argument is already date/time-family (date/datetime/smalldatetime/datetime2/
    /// datetimeoffset/time) - oracle-verified across all of those, each returning that same
    /// category unchanged (no widening). When the third argument is NOT date/time-family (an
    /// int, as in the common `DATEADD(day, DATEDIFF(day,0,x), 0)` truncation idiom, or a
    /// string literal/expression), the ENGINE implicitly converts it to a date and DATEADD
    /// returns plain `datetime` - also oracle-verified (a naive argument-passthrough rule here
    /// mistyped exactly this shape as Int/VarChar, which is what produced two of the four live
    /// lineage-parity mismatches this fix closes). See <see cref="ResolveDateAddResult"/>.
    /// </summary>
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
    };

    /// <summary>
    /// SUM/AVG - unlike MIN/MAX - widen a TINYINT/SMALLINT argument to INT (oracle-verified:
    /// SUM(TinyIntCol) and SUM(SmallIntCol) both return int; SUM(IntCol) stays int, SUM(BigIntCol)
    /// stays bigint; SUM(MoneyCol)/SUM(DecimalCol) keep their own category unchanged). See
    /// <see cref="WidenIntegerAggregateResult"/>.
    /// </summary>
    private static readonly HashSet<string> IntegerWideningAggregates = new(StringComparer.OrdinalIgnoreCase) { "SUM", "AVG" };

    /// <summary>
    /// LEFT/RIGHT/SUBSTRING/STUFF/REPLACE genuinely change the result's own declared length -
    /// unlike UPPER/LOWER/LTRIM/RTRIM/REVERSE, whose declared length is unchanged by the
    /// operation (only the runtime string content, never the declared max length, shortens).
    /// Passing through the SOURCE argument's own length here was a real bug: <c>LEFT(@p100, 3)</c>
    /// was typed <c>varchar(100)</c>, when the real result is <c>varchar(3)</c> - fabricating an
    /// Oversized-parameter finding where the truth is under-length, or vice versa. Computing the
    /// real per-function length would need this class to see the OTHER arguments too (a non-
    /// constant length argument makes even that unknowable), a larger change than this fix's
    /// scope - so instead this marks the result's Length unknown (<see cref="SqlType.LengthKnown"/>)
    /// rather than asserting the wrong one.
    /// </summary>
    private static readonly HashSet<string> LengthUnknownAfterArgumentType = new(StringComparer.OrdinalIgnoreCase)
    {
        "LEFT", "RIGHT", "SUBSTRING", "STUFF", "REPLACE",
    };

    /// <summary>True for LEFT/RIGHT/SUBSTRING/STUFF/REPLACE - the caller must pass the resolved argument type through <see cref="ClearLengthIfUnknown"/> rather than using it unmodified.</summary>
    public static bool ResultLengthDiffersFromArgument(string functionName) =>
        LengthUnknownAfterArgumentType.Contains(functionName);

    /// <summary>Nulls an already-resolved argument type's Length/LengthKnown - see <see cref="LengthUnknownAfterArgumentType"/>'s own doc comment for why the source argument's length can't just be reused.</summary>
    public static SqlType ClearLengthIfUnknown(SqlType argumentType) =>
        argumentType with { Length = null, LengthKnown = false };

    /// <summary>
    /// Every string-transform builtin this class models demotes a FIXED-width source category
    /// (char/nchar/binary) to its VARIABLE-width counterpart in the result - oracle-verified
    /// directly (Docker, sys.columns.TYPE_NAME off a SELECT ... INTO probe): <c>LEFT(charCol,
    /// 3)</c>, <c>UPPER(charCol)</c>, <c>SUBSTRING(charCol,1,2)</c>, <c>LTRIM/RTRIM(charCol)</c>,
    /// <c>REVERSE(charCol)</c>, <c>REPLACE(charCol,...)</c>, <c>RIGHT(charCol,3)</c>,
    /// <c>STUFF(charCol,...)</c> and <c>SUBSTRING(binaryCol,1,2)</c> all resolve varchar/
    /// varbinary, never char/binary - only the UNICODE-ness dimension (n-prefix) was previously
    /// verified for this table (this class's own remarks), the fixed-vs-variable-width dimension
    /// was simply never checked and silently passed the source category through unmodified.
    /// </summary>
    private static readonly HashSet<string> DemotesFixedWidthSourceCategory = new(StringComparer.OrdinalIgnoreCase)
    {
        "UPPER", "LOWER", "LTRIM", "RTRIM", "REVERSE", "REPLACE", "LEFT", "RIGHT", "SUBSTRING", "STUFF",
    };

    /// <summary>True for the string-transform builtins in <see cref="DemotesFixedWidthSourceCategory"/> - the caller must pass the resolved argument type through <see cref="DemoteFixedWidthCategory"/> rather than using its category unmodified.</summary>
    public static bool DemotesFixedWidthArgumentCategory(string functionName) =>
        DemotesFixedWidthSourceCategory.Contains(functionName);

    /// <summary>Demotes char/nchar/binary to varchar/nvarchar/varbinary on an already-resolved argument type; every other category passes through unchanged.</summary>
    public static SqlType DemoteFixedWidthCategory(SqlType argumentType) => argumentType.Category switch
    {
        SqlTypeCategory.Char => argumentType with { Category = SqlTypeCategory.VarChar },
        SqlTypeCategory.NChar => argumentType with { Category = SqlTypeCategory.NVarChar },
        SqlTypeCategory.Binary => argumentType with { Category = SqlTypeCategory.VarBinary },
        _ => argumentType,
    };

    /// <summary>True for DATEADD - the caller must resolve its third argument's type and pass it through <see cref="ResolveDateAddResult"/> rather than using it unmodified, since the passthrough only holds when that argument is already date/time-family.</summary>
    public static bool RequiresDateAddResultAdjustment(string functionName) =>
        string.Equals(functionName, "DATEADD", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Composes every argument-type-passthrough adjustment this table knows for a single-
    /// argument-typed builtin (<see cref="TryGetArgumentTypeIndex"/>), so every caller applies
    /// the exact same rule set rather than re-deriving the branching: SUM/AVG widening and
    /// DATEADD's own third-argument rule are each function-exclusive (only one can apply to a
    /// given name); the fixed-width demotion and length-unknown clearing are NOT mutually
    /// exclusive with each other (LEFT/RIGHT/SUBSTRING/STUFF/REPLACE need both; UPPER/LOWER/
    /// LTRIM/RTRIM/REVERSE only demote) but never apply alongside the first two. Null input
    /// (the argument itself never resolved) passes through unchanged.
    /// </summary>
    public static SqlType? AdjustArgumentTypeFunctionResult(string functionName, SqlType? argumentType)
    {
        if (argumentType is null)
        {
            return null;
        }

        if (WidensIntegerAggregateArgument(functionName))
        {
            return WidenIntegerAggregateResult(argumentType);
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

    /// <summary>Applies DATEADD's result-type rule to its already-resolved third argument: date/time-family types pass through unchanged; every other category (the engine implicitly converts a numeric or string date argument) resolves to plain <c>datetime</c>, oracle-verified.</summary>
    public static SqlType ResolveDateAddResult(SqlType thirdArgumentType) =>
        thirdArgumentType.IsDateTimeFamily
            ? thirdArgumentType
            : new SqlType(SqlTypeCategory.DateTime);

    /// <summary>The fixed return type for a built-in function call, or null if this function isn't in the curated table (never guessed).</summary>
    public static SqlType? ResolveFixedReturnType(string functionName) =>
        FixedReturnTypes.GetValueOrDefault(functionName);

    /// <summary>The 0-based argument index whose own type IS this function's return type (e.g. ISNULL/MIN/MAX at 0, DATEADD at 2), or null if this function isn't in the curated table - caller resolves that argument itself.</summary>
    public static int? TryGetArgumentTypeIndex(string functionName) =>
        ArgumentTypeFunctions.TryGetValue(functionName, out var index) ? index : null;

    /// <summary>True for SUM/AVG - the caller must resolve the (single) argument's type and pass it through <see cref="WidenIntegerAggregateResult"/> rather than using it unmodified.</summary>
    public static bool WidensIntegerAggregateArgument(string functionName) =>
        IntegerWideningAggregates.Contains(functionName);

    /// <summary>Applies SUM/AVG's integer-widening rule to an already-resolved argument type: TINYINT/SMALLINT widen to INT; every other category (INT, BIGINT, MONEY, DECIMAL, FLOAT, REAL) passes through unchanged.</summary>
    public static SqlType WidenIntegerAggregateResult(SqlType argumentType) =>
        argumentType.Category is SqlTypeCategory.TinyInt or SqlTypeCategory.SmallInt
            ? new SqlType(SqlTypeCategory.Int)
            : argumentType;

    /// <summary>The type of a <c>@@</c> global variable (e.g. <c>@@SPID</c>, <c>@@ROWCOUNT</c>), or null if it isn't in the curated table.</summary>
    public static SqlType? ResolveGlobalVariable(string name) =>
        GlobalVariableTypes.GetValueOrDefault(name);
}
