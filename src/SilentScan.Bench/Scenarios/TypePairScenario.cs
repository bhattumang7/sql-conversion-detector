using SilentScan.Core.Catalog;
using SilentScan.Core.TypeInference;

namespace SilentScan.Bench.Scenarios;

public sealed record TypePairScenario(
    string Name,
    string ColumnTypeDdl,
    string MatchedParamTypeDdl,
    string MismatchedParamTypeDdl,
    Func<int, string> MatchedParamValueForRow,
    Func<int, string> MismatchedParamValueForRow,
    string SeedValueExpression,
    SqlTypeCategory ColumnCategory,
    SqlTypeCategory MismatchedOtherCategory,
    Collation? Collation = null)
{
    public static TypePairScenario VarCharVsNVarChar(string collation) => new(
        Name: $"varchar_vs_nvarchar_{collation}",
        ColumnTypeDdl: $"VARCHAR(20) COLLATE {collation}",
        MatchedParamTypeDdl: "VARCHAR(20)",
        MismatchedParamTypeDdl: "NVARCHAR(20)",
        MatchedParamValueForRow: row => $"'ORD{row:D10}'",
        MismatchedParamValueForRow: row => $"N'ORD{row:D10}'",
        SeedValueExpression: "'ORD' + RIGHT('0000000000' + CAST(n AS VARCHAR(10)), 10)",
        ColumnCategory: SqlTypeCategory.VarChar,
        MismatchedOtherCategory: SqlTypeCategory.NVarChar,
        Collation: new Collation(collation));

    public static TypePairScenario IntVsBigInt() => new(
        Name: "int_vs_bigint",
        ColumnTypeDdl: "INT",
        MatchedParamTypeDdl: "INT",
        MismatchedParamTypeDdl: "BIGINT",
        MatchedParamValueForRow: row => row.ToString(System.Globalization.CultureInfo.InvariantCulture),
        MismatchedParamValueForRow: row => row.ToString(System.Globalization.CultureInfo.InvariantCulture),
        SeedValueExpression: "n",
        ColumnCategory: SqlTypeCategory.Int,
        MismatchedOtherCategory: SqlTypeCategory.BigInt);
}
