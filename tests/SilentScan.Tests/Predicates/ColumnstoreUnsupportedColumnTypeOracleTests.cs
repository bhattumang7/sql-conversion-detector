using Microsoft.Data.SqlClient;
using SilentScan.Tests.Support;

namespace SilentScan.Tests.Predicates;

[Trait("Category", "Oracle")]
public sealed class ColumnstoreUnsupportedColumnTypeOracleTests : OracleTestFixture
{
    protected override string DatabaseNameSeed => nameof(ColumnstoreUnsupportedColumnTypeOracleTests);

    protected override string Ddl => string.Empty;

    private async Task<SqlException> AssertDeployFailsAsync(string ddl)
    {
        await using var connection = new SqlConnection(Options.BuildConnectionString(DatabaseName));
        await connection.OpenAsync();

        await using var command = new SqlCommand(ddl, connection);
        return await Assert.ThrowsAsync<SqlException>(() => command.ExecuteNonQueryAsync());
    }

    private async Task AssertDeploySucceedsAsync(string ddl)
    {
        await using var connection = new SqlConnection(Options.BuildConnectionString(DatabaseName));
        await connection.OpenAsync();

        await using var command = new SqlCommand(ddl, connection);
        await command.ExecuteNonQueryAsync();
    }

    [Fact]
    public async Task ClusteredColumnstoreIndex_OnTableWithSqlVariantColumn_FailsToDeploy()
    {
        var exception = await AssertDeployFailsAsync(
            """
            CREATE TABLE dbo.Sales (SaleId INT NOT NULL, Amount INT NOT NULL, LegacyTag SQL_VARIANT NULL);
            CREATE CLUSTERED COLUMNSTORE INDEX CCI_Sales ON dbo.Sales;
            """);

        Assert.Equal(35343, exception.Number);
    }

    [Fact]
    public async Task ClusteredColumnstoreIndex_ThenAlterTableAddSqlVariantColumn_FailsToDeploy()
    {
        await AssertDeploySucceedsAsync(
            """
            CREATE TABLE dbo.Orders (OrderId INT NOT NULL, Amount INT NOT NULL);
            CREATE CLUSTERED COLUMNSTORE INDEX CCI_Orders ON dbo.Orders;
            """);

        var exception = await AssertDeployFailsAsync("ALTER TABLE dbo.Orders ADD Tag SQL_VARIANT NULL;");

        Assert.Equal(35343, exception.Number);
    }

    [Fact]
    public async Task NonclusteredColumnstoreIndex_NamingSqlVariantColumnInItsOwnList_FailsToDeploy()
    {
        var exception = await AssertDeployFailsAsync(
            """
            CREATE TABLE dbo.Invoices (InvoiceId INT NOT NULL, Amount INT NOT NULL, LegacyTag SQL_VARIANT NULL);
            CREATE NONCLUSTERED COLUMNSTORE INDEX NCCI_Invoices ON dbo.Invoices (Amount, LegacyTag);
            """);

        Assert.Equal(35343, exception.Number);
    }

    [Fact]
    public async Task NonclusteredColumnstoreIndex_OmittingSqlVariantColumnFromItsOwnList_DeploysCleanly()
    {
        await AssertDeploySucceedsAsync(
            """
            CREATE TABLE dbo.Payments (PaymentId INT NOT NULL, Amount INT NOT NULL, LegacyTag SQL_VARIANT NULL);
            CREATE NONCLUSTERED COLUMNSTORE INDEX NCCI_Payments ON dbo.Payments (PaymentId, Amount);
            """);
    }

    [Theory]
    [InlineData("XML")]
    [InlineData("HIERARCHYID")]
    [InlineData("GEOMETRY")]
    [InlineData("GEOGRAPHY")]
    [InlineData("NTEXT")]
    [InlineData("TEXT")]
    [InlineData("IMAGE")]
    public async Task ClusteredColumnstoreIndex_OnTableWithColumnOfUnsupportedType_FailsToDeploy(string typeName)
    {
        var exception = await AssertDeployFailsAsync(
            $"""
            CREATE TABLE dbo.T1 (Id INT NOT NULL, Payload {typeName} NULL);
            CREATE CLUSTERED COLUMNSTORE INDEX CCI_T1 ON dbo.T1;
            """);

        Assert.Equal(35343, exception.Number);
    }

    [Fact]
    public async Task ClusteredColumnstoreIndex_OnTableWithRowversionColumn_FailsToDeploy()
    {
        var exception = await AssertDeployFailsAsync(
            """
            CREATE TABLE dbo.T1 (Id INT NOT NULL, Payload ROWVERSION NOT NULL);
            CREATE CLUSTERED COLUMNSTORE INDEX CCI_T1 ON dbo.T1;
            """);

        Assert.Equal(35343, exception.Number);
    }

    [Theory]
    [InlineData("VARCHAR(MAX)")]
    [InlineData("NVARCHAR(MAX)")]
    [InlineData("VARBINARY(MAX)")]
    public async Task ClusteredColumnstoreIndex_OnTableWithMaxTypedColumn_DeploysCleanly(string typeName)
    {
        await AssertDeploySucceedsAsync(
            $"""
            CREATE TABLE dbo.T1 (Id INT NOT NULL, Payload {typeName} NULL);
            CREATE CLUSTERED COLUMNSTORE INDEX CCI_T1 ON dbo.T1;
            """);
    }

    [Theory]
    [InlineData("VARCHAR(MAX)")]
    [InlineData("NVARCHAR(MAX)")]
    [InlineData("VARBINARY(MAX)")]
    public async Task NonclusteredColumnstoreIndex_NamingMaxTypedColumnInItsOwnList_FailsToDeploy(string typeName)
    {
        var exception = await AssertDeployFailsAsync(
            $"""
            CREATE TABLE dbo.T1 (Id INT NOT NULL, Amount INT NOT NULL, Payload {typeName} NULL);
            CREATE NONCLUSTERED COLUMNSTORE INDEX NCCI_T1 ON dbo.T1 (Amount, Payload);
            """);

        Assert.Equal(35343, exception.Number);
    }

    [Fact]
    public async Task NonclusteredColumnstoreIndex_OmittingMaxTypedColumnFromItsOwnList_DeploysCleanly()
    {
        await AssertDeploySucceedsAsync(
            """
            CREATE TABLE dbo.T1 (Id INT NOT NULL, Amount INT NOT NULL, Payload VARCHAR(MAX) NULL);
            CREATE NONCLUSTERED COLUMNSTORE INDEX NCCI_T1 ON dbo.T1 (Id, Amount);
            """);
    }
}
