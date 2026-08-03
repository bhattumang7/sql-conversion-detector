using SilentScan.Core.Catalog;

namespace SilentScan.Bench.Scenarios;

/// <summary>
/// One reported type-pair to benchmark (CLAUDE.md Benchmark protocol): a column type/collation
/// and a query parameter that either matches it (SEEK_PRESERVED-shaped) or mismatches it
/// (the implicit-conversion case). The matched/mismatched param values must be seedable data
/// (present in the synthetic table) so the query returns a real, comparable plan.
/// <paramref name="ColumnCategory"/>/<paramref name="MismatchedOtherCategory"/>/<paramref name="Collation"/>
/// are the same category/collation facts <see cref="SilentScan.Core.Rules.VerdictClassifier"/> itself consumes -
/// carried here so a benchmark row can be stamped with the STATIC verdict it predicts, rather
/// than a reader having to cross-reference the matrix by hand to tell a row that confirms the
/// classifier apart from one that contradicts it.
/// </summary>
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
    /// <summary>The flagship CLAUDE.md example: varchar column vs nvarchar parameter.</summary>
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

    /// <summary>Numeric precedence example: int column vs bigint parameter.</summary>
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
