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
    /// Function names whose return type is exactly their FIRST argument's type - verified
    /// directly (ISNULL(1, @varcharVariable) returns int, not the higher-precedence varchar;
    /// ISNULL never applies data type precedence across its two arguments the way COALESCE
    /// does). Distinct from COALESCE/NULLIF, which CLAUDE.md's hard-cases list calls out as
    /// needing their own explicit precedence-aware rule - this is deliberately not that.
    /// </summary>
    private static readonly HashSet<string> FirstArgumentTypeFunctions = new(StringComparer.OrdinalIgnoreCase)
    {
        "ISNULL",
    };

    /// <summary>The fixed return type for a built-in function call, or null if this function isn't in the curated table (never guessed).</summary>
    public static SqlType? ResolveFixedReturnType(string functionName) =>
        FixedReturnTypes.GetValueOrDefault(functionName);

    /// <summary>True if <paramref name="functionName"/>'s return type is its first argument's own type (e.g. ISNULL) - caller resolves that argument itself.</summary>
    public static bool TakesFirstArgumentType(string functionName) =>
        FirstArgumentTypeFunctions.Contains(functionName);

    /// <summary>The type of a <c>@@</c> global variable (e.g. <c>@@SPID</c>, <c>@@ROWCOUNT</c>), or null if it isn't in the curated table.</summary>
    public static SqlType? ResolveGlobalVariable(string name) =>
        GlobalVariableTypes.GetValueOrDefault(name);
}
