using SilentScan.Core.Catalog;

namespace SilentScan.Core.Rules;

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
    /// (the source string) argument's own category AND collation exactly (probed:
    /// LOWER(nvarcharCol) returns nvarchar with the source's own collation; UPPER(varcharCol)
    /// returns varchar, collation unchanged). Length/precision facets are NOT preserved
    /// faithfully here (SQL Server computes a real per-function result length; this class
    /// returns the first argument's OWN declared length) - an accepted approximation, since
    /// <see cref="VerdictClassifier"/> never consults length/precision in a cross-category
    /// decision, only category and collation. CONCAT is deliberately NOT included - its return
    /// type depends on ALL arguments (nvarchar if any is unicode, varchar otherwise), a
    /// genuinely different, multi-argument rule this single-argument mechanism can't express;
    /// left as a documented gap rather than force-fit.
    ///
    /// MIN/MAX preserve their (only) argument's exact type unmodified - oracle-verified
    /// (MIN(TinyIntCol) returns tinyint, MAX(MoneyCol) returns money; unlike SUM/AVG below,
    /// neither widens an integer-family argument). DATEADD's return type follows its THIRD
    /// argument (the date expression, index 2, not its datepart keyword at index 0) - oracle-
    /// verified across date/datetime/smalldatetime/datetime2, each returning that same category
    /// unchanged (no widening the way SQL Server's own docs might suggest for smalldatetime/date
    /// inputs elsewhere).
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
