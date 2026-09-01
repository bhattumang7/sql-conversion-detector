using Microsoft.Data.SqlClient;
using SilentScan.Core.Catalog;
using SilentScan.Core.Parsing;
using SilentScan.Core.Predicates;
using SilentScan.Tests.Support;

namespace SilentScan.Tests.Integration;

[Trait("Category", "Oracle")]
public sealed class GroupingArgumentNotInGroupByListOracleTests : OracleTestFixture
{
    protected override string DatabaseNameSeed => nameof(GroupingArgumentNotInGroupByListOracleTests);

    protected override string Ddl => """
        CREATE TABLE dbo.Facts (RegionId INT NOT NULL, SalesYear INT NOT NULL, Amount INT NOT NULL);
        GO
        CREATE TABLE dbo.OtherFacts (RegionId INT NOT NULL, Amount INT NOT NULL);
        """;

    [Fact]
    public async Task NoGroupByClause_GroupingCall_RejectedWithMsg8161()
    {
        var exception = await Assert.ThrowsAsync<SqlException>(() =>
            ExecuteAsync("SELECT RegionId, GROUPING(RegionId) FROM dbo.Facts;"));

        Assert.Equal(8161, exception.Number);
    }

    [Fact]
    public async Task ColumnAbsentFromGroupByList_GroupingCall_RejectedWithMsg8161()
    {
        var exception = await Assert.ThrowsAsync<SqlException>(() =>
            ExecuteAsync("SELECT RegionId, GROUPING(SalesYear) FROM dbo.Facts GROUP BY RegionId;"));

        Assert.Equal(8161, exception.Number);

        var findings = Scan("SELECT RegionId, GROUPING(SalesYear) FROM dbo.Facts GROUP BY RegionId;");

        var finding = Assert.Single(findings, f => f.Kind == QueryAntiPatternFindingKind.GroupingArgumentNotInGroupByList);
        Assert.Equal(FindingConfidence.High, finding.Confidence);
        Assert.Contains("8161", finding.DetailText);
    }

    [Fact]
    public async Task ColumnPresentInGroupByList_GroupingCall_Compiles()
    {
        await ExecuteAsync("SELECT RegionId, GROUPING(RegionId) FROM dbo.Facts GROUP BY RegionId;");

        var findings = Scan("SELECT RegionId, GROUPING(RegionId) FROM dbo.Facts GROUP BY RegionId;");

        Assert.DoesNotContain(findings, f => f.Kind == QueryAntiPatternFindingKind.GroupingArgumentNotInGroupByList);
    }

    [Fact]
    public async Task QualifiedGroupingArgumentAgainstUnqualifiedGroupByColumn_Compiles()
    {
        await ExecuteAsync("SELECT f.RegionId, GROUPING(f.RegionId) FROM dbo.Facts f GROUP BY RegionId;");

        var findings = Scan("SELECT f.RegionId, GROUPING(f.RegionId) FROM dbo.Facts f GROUP BY RegionId;");

        Assert.DoesNotContain(findings, f => f.Kind == QueryAntiPatternFindingKind.GroupingArgumentNotInGroupByList);
    }

    [Fact]
    public async Task ColumnFromUnjoinedTableSharingName_RejectedWithMsg8161()
    {
        var exception = await Assert.ThrowsAsync<SqlException>(() => ExecuteAsync(
            "SELECT f.RegionId, GROUPING(o.RegionId) "
            + "FROM dbo.Facts f JOIN dbo.OtherFacts o ON f.RegionId = o.RegionId "
            + "GROUP BY f.RegionId;"));

        Assert.Equal(8161, exception.Number);
    }

    [Fact]
    public async Task RollupOverAllGroupedColumns_Compiles()
    {
        await ExecuteAsync(
            "SELECT RegionId, SalesYear, GROUPING(RegionId), GROUPING(SalesYear) "
            + "FROM dbo.Facts GROUP BY ROLLUP(RegionId, SalesYear);");

        var findings = Scan(
            "SELECT RegionId, SalesYear, GROUPING(RegionId), GROUPING(SalesYear) "
            + "FROM dbo.Facts GROUP BY ROLLUP(RegionId, SalesYear);");

        Assert.DoesNotContain(findings, f => f.Kind == QueryAntiPatternFindingKind.GroupingArgumentNotInGroupByList);
    }

    [Fact]
    public async Task GroupingSetsColumnAbsentFromEverySet_RejectedWithMsg8161()
    {
        var exception = await Assert.ThrowsAsync<SqlException>(() => ExecuteAsync(
            "SELECT RegionId, GROUPING(RegionId), GROUPING(Amount) "
            + "FROM dbo.Facts GROUP BY GROUPING SETS ((RegionId), (SalesYear));"));

        Assert.Equal(8161, exception.Number);

        var findings = Scan(
            "SELECT RegionId, GROUPING(RegionId), GROUPING(Amount) "
            + "FROM dbo.Facts GROUP BY GROUPING SETS ((RegionId), (SalesYear));");

        var finding = Assert.Single(findings, f => f.Kind == QueryAntiPatternFindingKind.GroupingArgumentNotInGroupByList);
        Assert.Contains("Amount", finding.DetailText);
    }

    [Fact]
    public async Task GroupingIdSecondArgumentAbsentFromGroupByList_RejectedWithMsg8161()
    {
        var exception = await Assert.ThrowsAsync<SqlException>(() => ExecuteAsync(
            "SELECT RegionId, SalesYear, GROUPING_ID(RegionId, Amount) "
            + "FROM dbo.Facts GROUP BY RegionId, SalesYear;"));

        Assert.Equal(8161, exception.Number);

        var findings = Scan(
            "SELECT RegionId, SalesYear, GROUPING_ID(RegionId, Amount) "
            + "FROM dbo.Facts GROUP BY RegionId, SalesYear;");

        var finding = Assert.Single(findings, f => f.Kind == QueryAntiPatternFindingKind.GroupingArgumentNotInGroupByList);
        Assert.Contains("Amount", finding.DetailText);
    }

    [Fact]
    public async Task HavingClauseGroupingCall_ColumnAbsentFromGroupByList_RejectedWithMsg8161()
    {
        var exception = await Assert.ThrowsAsync<SqlException>(() => ExecuteAsync(
            "SELECT RegionId FROM dbo.Facts GROUP BY RegionId HAVING GROUPING(SalesYear) = 0;"));

        Assert.Equal(8161, exception.Number);

        var findings = Scan("SELECT RegionId FROM dbo.Facts GROUP BY RegionId HAVING GROUPING(SalesYear) = 0;");

        Assert.Single(findings, f => f.Kind == QueryAntiPatternFindingKind.GroupingArgumentNotInGroupByList);
    }

    [Fact]
    public async Task OrderByClauseGroupingCall_ColumnAbsentFromGroupByList_RejectedWithMsg8161()
    {
        var exception = await Assert.ThrowsAsync<SqlException>(() => ExecuteAsync(
            "SELECT RegionId FROM dbo.Facts GROUP BY RegionId ORDER BY GROUPING(SalesYear);"));

        Assert.Equal(8161, exception.Number);

        var findings = Scan("SELECT RegionId FROM dbo.Facts GROUP BY RegionId ORDER BY GROUPING(SalesYear);");

        Assert.Single(findings, f => f.Kind == QueryAntiPatternFindingKind.GroupingArgumentNotInGroupByList);
    }

    [Fact]
    public async Task ExpressionGroupingArgument_NotFlagged()
    {
        await ExecuteAsync("SELECT RegionId + 1, GROUPING(RegionId + 1) FROM dbo.Facts GROUP BY RegionId + 1;");

        var findings = Scan("SELECT RegionId + 1, GROUPING(RegionId + 1) FROM dbo.Facts GROUP BY RegionId + 1;");

        Assert.DoesNotContain(findings, f => f.Kind == QueryAntiPatternFindingKind.GroupingArgumentNotInGroupByList);
    }

    private static IReadOnlyList<QueryAntiPatternFinding> Scan(string sql)
    {
        var result = SqlScriptParser.ParseText("test.sql", sql);
        Assert.False(result.HasErrors, string.Join("; ", result.Errors.Select(e => e.Message)));

        var catalog = new DatabaseCatalog();
        return QueryAntiPatternScanner.Scan(result, catalog);
    }

    private async Task ExecuteAsync(string sql)
    {
        await using var connection = new SqlConnection(Options.BuildConnectionString(DatabaseName));
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync();
    }
}
