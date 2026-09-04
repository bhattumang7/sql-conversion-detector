using Microsoft.Data.SqlClient;
using SilentScan.Core.Catalog;
using SilentScan.Core.Parsing;
using SilentScan.Core.Predicates;
using SilentScan.Tests.Support;

namespace SilentScan.Tests.Integration;

[Trait("Category", "Oracle")]
public sealed class XmlSchemaCollectionMismatchOracleTests : OracleTestFixture
{
    protected override string DatabaseNameSeed => nameof(XmlSchemaCollectionMismatchOracleTests);

    protected override string Ddl => """
        CREATE XML SCHEMA COLLECTION OrderSchema AS N'<xs:schema xmlns:xs="http://www.w3.org/2001/XMLSchema"><xs:element name="Order" type="xs:string"/></xs:schema>';
        GO
        CREATE XML SCHEMA COLLECTION InvoiceSchema AS N'<xs:schema xmlns:xs="http://www.w3.org/2001/XMLSchema"><xs:element name="Invoice" type="xs:string"/></xs:schema>';
        """;

    private static IReadOnlyList<XmlSchemaCollectionMismatchFinding> Scan(string sql)
    {
        var result = SqlScriptParser.ParseText("test.sql", sql);
        Assert.False(result.HasErrors, string.Join("; ", result.Errors.Select(e => e.Message)));
        var catalog = CatalogBuilder.Build([result]);
        return XmlSchemaCollectionMismatchScanner.Scan(result, catalog);
    }

    private async Task<SqlException> ExecuteExpectingFailureAsync(string sql)
    {
        await using var connection = new SqlConnection(Options.BuildConnectionString(DatabaseName));
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        return await Assert.ThrowsAsync<SqlException>(() => command.ExecuteNonQueryAsync());
    }

    private async Task ExecuteAsync(string sql)
    {
        await using var connection = new SqlConnection(Options.BuildConnectionString(DatabaseName));
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync();
    }

    [Fact]
    public async Task SetAssignment_BetweenDifferentSchemaCollections_FailsToCompileWithMsg527_AndScannerFlagsIt()
    {
        const string Sql = """
            DECLARE @order XML(dbo.OrderSchema) = '<Order>x</Order>';
            DECLARE @invoice XML(dbo.InvoiceSchema);
            SET @invoice = @order;
            """;

        var exception = await ExecuteExpectingFailureAsync(Sql);
        Assert.Equal(527, exception.Number);

        var finding = Assert.Single(Scan(Sql));
        Assert.Equal("@invoice", finding.TargetVariableName);
        Assert.Equal("@order", finding.SourceVariableName);
    }

    [Fact]
    public async Task SetAssignment_BetweenSameSchemaCollection_Succeeds_AndScannerDoesNotFlagIt()
    {
        const string Sql = """
            DECLARE @a XML(dbo.OrderSchema) = '<Order>x</Order>';
            DECLARE @b XML(dbo.OrderSchema);
            SET @b = @a;
            SELECT @b;
            """;

        await ExecuteAsync(Sql);

        Assert.Empty(Scan(Sql));
    }

    [Fact]
    public async Task SetAssignment_ThroughConvert_Succeeds_AndScannerDoesNotFlagIt()
    {
        const string Sql = """
            DECLARE @order XML(dbo.OrderSchema) = '<Order>x</Order>';
            DECLARE @invoice XML(dbo.InvoiceSchema);
            SET @invoice = CONVERT(XML(dbo.InvoiceSchema), CAST(@order AS NVARCHAR(MAX)));
            SELECT @invoice;
            """;

        var exception = await Record.ExceptionAsync(() => ExecuteAsync(Sql));

        Assert.True(exception is null or SqlException { Number: not 527 });
        Assert.Empty(Scan(Sql));
    }
}
