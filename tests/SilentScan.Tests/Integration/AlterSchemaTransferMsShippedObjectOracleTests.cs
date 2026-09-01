using Microsoft.Data.SqlClient;
using SilentScan.Core.Parsing;
using SilentScan.Core.Predicates;
using SilentScan.Tests.Support;
using SilentScan.Verify.Catalog;

namespace SilentScan.Tests.Integration;

[Trait("Category", "Oracle")]
public sealed class AlterSchemaTransferMsShippedObjectOracleTests : OracleTestFixture
{
    protected override string DatabaseNameSeed => nameof(AlterSchemaTransferMsShippedObjectOracleTests);

    protected override string Ddl => """
        CREATE SCHEMA target_schema;
        GO
        CREATE TABLE dbo.OrdinaryTable (Id INT NOT NULL PRIMARY KEY);
        """;

    [Fact]
    public async Task MsShippedObject_BlocksTransferWith15349()
    {
        var exception = await Assert.ThrowsAsync<SqlException>(() => ExecuteAsync("ALTER SCHEMA target_schema TRANSFER OBJECT::dbo.ServiceBrokerQueue;"));

        Assert.Equal(15349, exception.Number);
        Assert.Contains("MS Shipped", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task OrdinaryUserTable_TransferSucceeds()
    {
        await ExecuteAsync("ALTER SCHEMA target_schema TRANSFER OBJECT::dbo.OrdinaryTable;");

        var catalog = await new LiveCatalogReader(Options.BuildConnectionString(DatabaseName)).ReadAsync();
        Assert.NotNull(catalog.Find("target_schema.OrdinaryTable"));
    }

    [Fact]
    public async Task LiveCatalogAndScanner_ReportTheMsShippedTarget()
    {
        var catalog = await new LiveCatalogReader(Options.BuildConnectionString(DatabaseName)).ReadAsync();
        var result = SqlScriptParser.ParseText("test.sql", "ALTER SCHEMA target_schema TRANSFER OBJECT::dbo.ServiceBrokerQueue;");

        Assert.False(result.HasErrors, string.Join("; ", result.Errors.Select(e => e.Message)));
        Assert.True(catalog.IsMsShippedObject("dbo.ServiceBrokerQueue"));
        var finding = Assert.Single(QueryAntiPatternScanner.Scan(result, catalog), f => f.Kind == QueryAntiPatternFindingKind.AlterSchemaTransferMsShippedObject);
        Assert.Equal(FindingConfidence.High, finding.Confidence);
        Assert.Contains("15349", finding.DetailText);
    }

    [Fact]
    public async Task LiveCatalogAndScanner_NeverFireOnOrdinaryUserTable()
    {
        var catalog = await new LiveCatalogReader(Options.BuildConnectionString(DatabaseName)).ReadAsync();
        var result = SqlScriptParser.ParseText("test.sql", "ALTER SCHEMA target_schema TRANSFER OBJECT::dbo.OrdinaryTable;");

        Assert.False(result.HasErrors, string.Join("; ", result.Errors.Select(e => e.Message)));
        Assert.False(catalog.IsMsShippedObject("dbo.OrdinaryTable"));
        Assert.DoesNotContain(QueryAntiPatternScanner.Scan(result, catalog), f => f.Kind == QueryAntiPatternFindingKind.AlterSchemaTransferMsShippedObject);
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
