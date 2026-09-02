using Microsoft.Data.SqlClient;
using SilentScan.Core.Parsing;
using SilentScan.Core.Predicates;
using SilentScan.Tests.Support;
using SilentScan.Verify.Catalog;

namespace SilentScan.Tests.Integration;

[Trait("Category", "Oracle")]
public sealed class PartitionRebuildOracleTests : OracleTestFixture
{
    protected override string DatabaseNameSeed => nameof(PartitionRebuildOracleTests);

    protected override string Ddl => """
        CREATE PARTITION FUNCTION PfRebuild (int) AS RANGE LEFT FOR VALUES (10, 20, 30);
        GO
        CREATE PARTITION SCHEME PsRebuild AS PARTITION PfRebuild ALL TO ([PRIMARY]);
        GO
        CREATE TABLE dbo.RebuildTarget (Id INT NOT NULL, Grp INT NOT NULL) ON PsRebuild(Grp);
        GO
        CREATE CLUSTERED INDEX IX_RebuildTarget ON dbo.RebuildTarget(Grp, Id) ON PsRebuild(Grp);
        """;

    [Fact]
    public async Task AlterTableRebuild_PartitionNumberWithinSchemeRange_Succeeds()
    {
        var exception = await Record.ExceptionAsync(() => ExecuteAsync("ALTER TABLE dbo.RebuildTarget REBUILD PARTITION = 4;"));

        Assert.Null(exception);
    }

    [Fact]
    public async Task LiveCatalogAndScanner_DoesNotReportPartitionNumberWithinSchemeRange()
    {
        var catalog = await new LiveCatalogReader(Options.BuildConnectionString(DatabaseName)).ReadAsync();
        var result = SqlScriptParser.ParseText("test.sql", "ALTER TABLE dbo.RebuildTarget REBUILD PARTITION = 4;");

        Assert.False(result.HasErrors, string.Join("; ", result.Errors.Select(e => e.Message)));
        Assert.DoesNotContain(
            QueryAntiPatternScanner.Scan(result, catalog),
            f => f.Kind is QueryAntiPatternFindingKind.AlterTableRebuildPartitionOutOfRange or QueryAntiPatternFindingKind.PartitionRebuildNumberExceedsCeiling);
    }

    [Fact]
    public async Task AlterTableRebuild_PartitionNumberAboveSchemeRange_BlocksWith7730()
    {
        var exception = await Assert.ThrowsAsync<SqlException>(
            () => ExecuteAsync("ALTER TABLE dbo.RebuildTarget REBUILD PARTITION = 5;"));

        Assert.Equal(7730, exception.Number);
    }

    [Fact]
    public async Task LiveCatalogAndScanner_ReportsPartitionNumberAboveSchemeRange()
    {
        var catalog = await new LiveCatalogReader(Options.BuildConnectionString(DatabaseName)).ReadAsync();
        var result = SqlScriptParser.ParseText("test.sql", "ALTER TABLE dbo.RebuildTarget REBUILD PARTITION = 5;");

        Assert.False(result.HasErrors, string.Join("; ", result.Errors.Select(e => e.Message)));
        var finding = Assert.Single(
            QueryAntiPatternScanner.Scan(result, catalog), f => f.Kind == QueryAntiPatternFindingKind.AlterTableRebuildPartitionOutOfRange);
        Assert.Equal(FindingConfidence.High, finding.Confidence);
        Assert.Contains("7730", finding.DetailText);
    }

    [Fact]
    public async Task AlterIndexRebuild_PartitionNumberAboveSchemeRange_BlocksWith7730()
    {
        var exception = await Assert.ThrowsAsync<SqlException>(
            () => ExecuteAsync("ALTER INDEX IX_RebuildTarget ON dbo.RebuildTarget REBUILD PARTITION = 5;"));

        Assert.Equal(7730, exception.Number);
    }

    [Fact]
    public async Task AlterTableRebuild_PartitionNumberAboveUniversalCeiling_BlocksWith7722()
    {
        var exception = await Assert.ThrowsAsync<SqlException>(
            () => ExecuteAsync("ALTER TABLE dbo.RebuildTarget REBUILD PARTITION = 15001;"));

        Assert.Equal(7722, exception.Number);
    }

    [Fact]
    public async Task LiveCatalogAndScanner_ReportsPartitionNumberAboveUniversalCeiling()
    {
        var catalog = await new LiveCatalogReader(Options.BuildConnectionString(DatabaseName)).ReadAsync();
        var result = SqlScriptParser.ParseText("test.sql", "ALTER TABLE dbo.RebuildTarget REBUILD PARTITION = 15001;");

        Assert.False(result.HasErrors, string.Join("; ", result.Errors.Select(e => e.Message)));
        var finding = Assert.Single(
            QueryAntiPatternScanner.Scan(result, catalog), f => f.Kind == QueryAntiPatternFindingKind.PartitionRebuildNumberExceedsCeiling);
        Assert.Equal(FindingConfidence.High, finding.Confidence);
        Assert.Contains("7722", finding.DetailText);
    }

    [Fact]
    public async Task AlterIndexRebuild_PartitionNumberAboveUniversalCeiling_BlocksWith7722()
    {
        var exception = await Assert.ThrowsAsync<SqlException>(
            () => ExecuteAsync("ALTER INDEX IX_RebuildTarget ON dbo.RebuildTarget REBUILD PARTITION = 15001;"));

        Assert.Equal(7722, exception.Number);
    }

    [Fact]
    public async Task LiveCatalogAndScanner_ReportsAlterIndexPartitionNumberAboveUniversalCeiling()
    {
        var catalog = await new LiveCatalogReader(Options.BuildConnectionString(DatabaseName)).ReadAsync();
        var result = SqlScriptParser.ParseText("test.sql", "ALTER INDEX IX_RebuildTarget ON dbo.RebuildTarget REBUILD PARTITION = 15001;");

        Assert.False(result.HasErrors, string.Join("; ", result.Errors.Select(e => e.Message)));
        var finding = Assert.Single(
            QueryAntiPatternScanner.Scan(result, catalog), f => f.Kind == QueryAntiPatternFindingKind.PartitionRebuildNumberExceedsCeiling);
        Assert.Equal(FindingConfidence.High, finding.Confidence);
        Assert.Contains("7722", finding.DetailText);
    }

    private async Task ExecuteAsync(string sql)
    {
        await using var connection = new SqlConnection(Options.BuildConnectionString(DatabaseName));
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.CommandTimeout = 60;
        await command.ExecuteNonQueryAsync();
    }
}
