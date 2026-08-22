using Microsoft.Data.SqlClient;
using SilentScan.Core.Parsing;
using SilentScan.Core.Predicates;
using SilentScan.Tests.Support;
using SilentScan.Verify.Catalog;

namespace SilentScan.Tests.Integration;

[Trait("Category", "Oracle")]
public sealed class AlterTableSwitchFullTextIndexOracleTests : OracleTestFixture
{
    protected override string DatabaseNameSeed => nameof(AlterTableSwitchFullTextIndexOracleTests);

    protected override string Ddl => """
        CREATE FULLTEXT CATALOG SwitchCatalog AS DEFAULT;
        GO
        CREATE TABLE dbo.SourceIndexed (Id INT NOT NULL CONSTRAINT PK_SourceIndexed PRIMARY KEY, Body NVARCHAR(4000) NOT NULL);
        GO
        CREATE TABLE dbo.TargetPlain (Id INT NOT NULL CONSTRAINT PK_TargetPlain PRIMARY KEY, Body NVARCHAR(4000) NOT NULL);
        GO
        CREATE TABLE dbo.SourcePlain (Id INT NOT NULL CONSTRAINT PK_SourcePlain PRIMARY KEY, Body NVARCHAR(4000) NOT NULL);
        GO
        CREATE TABLE dbo.TargetIndexed (Id INT NOT NULL CONSTRAINT PK_TargetIndexed PRIMARY KEY, Body NVARCHAR(4000) NOT NULL);
        GO
        CREATE FULLTEXT INDEX ON dbo.SourceIndexed(Body) KEY INDEX PK_SourceIndexed;
        GO
        CREATE FULLTEXT INDEX ON dbo.TargetIndexed(Body) KEY INDEX PK_TargetIndexed;
        """;

    [Fact]
    public async Task SourceFullTextIndex_BlocksSwitchWith4918()
    {
        var exception = await Assert.ThrowsAsync<SqlException>(() => ExecuteAsync("ALTER TABLE dbo.SourceIndexed SWITCH TO dbo.TargetPlain;"));

        Assert.Equal(4918, exception.Number);
        Assert.Contains("fulltext index", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task TargetFullTextIndex_BlocksSwitchWith4918()
    {
        var exception = await Assert.ThrowsAsync<SqlException>(() => ExecuteAsync("ALTER TABLE dbo.SourcePlain SWITCH TO dbo.TargetIndexed;"));

        Assert.Equal(4918, exception.Number);
        Assert.Contains("fulltext index", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task LiveCatalogAndScanner_ReportTheIndexedSource()
    {
        var catalog = await new LiveCatalogReader(Options.BuildConnectionString(DatabaseName)).ReadAsync();
        var result = SqlScriptParser.ParseText("test.sql", "ALTER TABLE dbo.SourceIndexed SWITCH TO dbo.TargetPlain;");

        Assert.False(result.HasErrors, string.Join("; ", result.Errors.Select(e => e.Message)));
        Assert.True(catalog.Find("dbo.SourceIndexed")!.HasFullTextIndex);
        Assert.False(catalog.Find("dbo.TargetPlain")!.HasFullTextIndex);
        var finding = Assert.Single(QueryAntiPatternScanner.Scan(result, catalog), f => f.Kind == QueryAntiPatternFindingKind.AlterTableSwitchFullTextIndexRestriction);
        Assert.Equal(FindingConfidence.High, finding.Confidence);
        Assert.Contains("4918", finding.DetailText);
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
