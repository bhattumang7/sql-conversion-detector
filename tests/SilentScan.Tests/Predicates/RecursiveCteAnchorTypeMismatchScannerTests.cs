using Microsoft.Data.SqlClient;
using SilentScan.Core.Lineage;
using SilentScan.Core.Parsing;
using SilentScan.Core.Predicates;
using SilentScan.Tests.Support;
using SilentScan.Verify.Catalog;

namespace SilentScan.Tests.Predicates;

[Trait("Category", "Oracle")]
public sealed class RecursiveCteAnchorTypeMismatchScannerTests : OracleTestFixture
{
    protected override string DatabaseNameSeed => nameof(RecursiveCteAnchorTypeMismatchScannerTests);

    protected override string Ddl => """
        CREATE TABLE dbo.Categories (
            CategoryCode VARCHAR(20) COLLATE Latin1_General_CI_AS NOT NULL,
            ParentCode VARCHAR(20) NULL,
            AltCode VARCHAR(20) COLLATE Latin1_General_CS_AS NOT NULL);
        GO
        """;

    private static readonly IReadOnlyDictionary<string, ResolvedRelation> EmptyResolvedViews = new Dictionary<string, ResolvedRelation>();

    private async Task<IReadOnlyList<RecursiveCteAnchorTypeMismatchFinding>> ScanAsync(string sql)
    {
        var catalog = await new LiveCatalogReader(Options.BuildConnectionString(DatabaseName)).ReadAsync();
        var result = SqlScriptParser.ParseText("test.sql", sql);
        Assert.False(result.HasErrors, string.Join("; ", result.Errors.Select(e => e.Message)));

        return RecursiveCteAnchorTypeMismatchScanner.Scan(result, catalog, EmptyResolvedViews);
    }

    [Fact]
    public async Task EngineFact_MismatchedAnchorRecursiveVarcharLength_RaisesMsg240AtCompileTime()
    {
        await using var connection = new SqlConnection(Options.BuildConnectionString(DatabaseName));
        await connection.OpenAsync();

        await using var command = new SqlCommand(
            """
            WITH Tree AS (
                SELECT CAST(CategoryCode AS VARCHAR(20)) AS CategoryCode FROM dbo.Categories WHERE ParentCode IS NULL
                UNION ALL
                SELECT CAST(t.CategoryCode AS VARCHAR(5)) FROM Tree t
            )
            SELECT CategoryCode FROM Tree OPTION (MAXRECURSION 1);
            """, connection);

        var exception = await Assert.ThrowsAsync<SqlException>(() => command.ExecuteNonQueryAsync());
        Assert.Equal(240, exception.Number);
    }

    [Fact]
    public async Task EngineFact_MatchingAnchorRecursiveVarcharLength_CompilesAndRuns()
    {
        await using var connection = new SqlConnection(Options.BuildConnectionString(DatabaseName));
        await connection.OpenAsync();

        await using var command = new SqlCommand(
            """
            WITH Tree AS (
                SELECT CAST(CategoryCode AS VARCHAR(20)) AS CategoryCode FROM dbo.Categories WHERE ParentCode IS NULL
                UNION ALL
                SELECT CAST(t.CategoryCode AS VARCHAR(20)) FROM Tree t
            )
            SELECT CategoryCode FROM Tree OPTION (MAXRECURSION 1);
            """, connection);

        var exception = await Record.ExceptionAsync(() => command.ExecuteNonQueryAsync());
        Assert.Null(exception);
    }

    [Fact]
    public async Task RecursiveMemberNarrowsVarcharLength_Fires()
    {
        var findings = await ScanAsync(
            """
            WITH Tree AS (
                SELECT CAST(CategoryCode AS VARCHAR(20)) AS CategoryCode FROM dbo.Categories WHERE ParentCode IS NULL
                UNION ALL
                SELECT CAST(t.CategoryCode AS VARCHAR(5)) FROM Tree t
            )
            SELECT CategoryCode FROM Tree;
            """);

        var finding = Assert.Single(findings);
        Assert.Equal("Tree", finding.CteName);
        Assert.Equal("CategoryCode", finding.ColumnName);
        Assert.Contains("20", finding.AnchorTypeDisplay, StringComparison.Ordinal);
        Assert.Contains("5", finding.RecursiveTypeDisplay, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RecursiveMemberChangesCategoryFromVarcharToInt_Fires()
    {
        var findings = await ScanAsync(
            """
            WITH Tree AS (
                SELECT CAST(CategoryCode AS VARCHAR(20)) AS CategoryCode FROM dbo.Categories WHERE ParentCode IS NULL
                UNION ALL
                SELECT CAST(1 AS INT) FROM Tree t
            )
            SELECT CategoryCode FROM Tree;
            """);

        var finding = Assert.Single(findings);
        Assert.Equal("CategoryCode", finding.ColumnName);
        Assert.Contains("VarChar", finding.AnchorTypeDisplay, StringComparison.Ordinal);
        Assert.Contains("Int", finding.RecursiveTypeDisplay, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RecursiveMemberChangesDecimalPrecisionAndScale_Fires()
    {
        var findings = await ScanAsync(
            """
            WITH Tree AS (
                SELECT CAST(1 AS DECIMAL(10,2)) AS Val FROM dbo.Categories WHERE ParentCode IS NULL
                UNION ALL
                SELECT CAST(t.Val AS DECIMAL(10,3)) FROM Tree t
            )
            SELECT Val FROM Tree;
            """);

        var finding = Assert.Single(findings);
        Assert.Equal("Val", finding.ColumnName);
        Assert.Contains("10,2", finding.AnchorTypeDisplay, StringComparison.Ordinal);
        Assert.Contains("10,3", finding.RecursiveTypeDisplay, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RecursiveMemberChangesCollation_Fires()
    {
        var findings = await ScanAsync(
            """
            WITH Tree AS (
                SELECT CategoryCode FROM dbo.Categories WHERE ParentCode IS NULL
                UNION ALL
                SELECT c.AltCode FROM dbo.Categories c CROSS JOIN Tree t
            )
            SELECT CategoryCode FROM Tree;
            """);

        var finding = Assert.Single(findings);
        Assert.Equal("CategoryCode", finding.ColumnName);
        Assert.Contains("Latin1_General_CI_AS", finding.AnchorTypeDisplay, StringComparison.Ordinal);
        Assert.Contains("Latin1_General_CS_AS", finding.RecursiveTypeDisplay, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RecursiveMemberMatchesAnchorExactly_NeverFires()
    {
        var findings = await ScanAsync(
            """
            WITH Tree AS (
                SELECT CAST(CategoryCode AS VARCHAR(20)) AS CategoryCode FROM dbo.Categories WHERE ParentCode IS NULL
                UNION ALL
                SELECT CAST(t.CategoryCode AS VARCHAR(20)) FROM Tree t
            )
            SELECT CategoryCode FROM Tree;
            """);

        Assert.Empty(findings);
    }

    [Fact]
    public async Task RecursiveMemberDiffersOnlyByNullability_NeverFires()
    {
        var findings = await ScanAsync(
            """
            WITH Tree AS (
                SELECT CAST(CategoryCode AS VARCHAR(20)) AS CategoryCode FROM dbo.Categories WHERE ParentCode IS NULL
                UNION ALL
                SELECT CAST(NULL AS VARCHAR(20)) FROM Tree t WHERE 1 = 0
            )
            SELECT CategoryCode FROM Tree;
            """);

        Assert.Empty(findings);
    }

    [Fact]
    public async Task ThreeMemberRecursiveCte_OnlyFlagsTheMismatchedMember()
    {
        var findings = await ScanAsync(
            """
            WITH Tree AS (
                SELECT CAST(CategoryCode AS VARCHAR(20)) AS CategoryCode FROM dbo.Categories WHERE ParentCode IS NULL
                UNION ALL
                SELECT CAST(t.CategoryCode AS VARCHAR(20)) FROM Tree t
                UNION ALL
                SELECT CAST(t2.CategoryCode AS VARCHAR(9)) FROM Tree t2
            )
            SELECT CategoryCode FROM Tree;
            """);

        var finding = Assert.Single(findings);
        Assert.Contains("9", finding.RecursiveTypeDisplay, StringComparison.Ordinal);
    }

    [Fact]
    public async Task NonRecursiveCte_NeverFires()
    {
        var findings = await ScanAsync("WITH cte AS (SELECT CategoryCode FROM dbo.Categories) SELECT CategoryCode FROM cte;");

        Assert.Empty(findings);
    }

    [Fact]
    public async Task NoWithClause_NeverFires()
    {
        var findings = await ScanAsync("SELECT CategoryCode FROM dbo.Categories;");

        Assert.Empty(findings);
    }
}
