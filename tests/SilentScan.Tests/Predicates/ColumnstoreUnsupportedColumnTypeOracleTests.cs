using Microsoft.Data.SqlClient;
using SilentScan.Tests.Support;

namespace SilentScan.Tests.Predicates;

/// <summary>
/// docs/detection-reference.md Appendix 8 - oracle-confirms the real DDL-time failure
/// <see cref="ColumnstoreUnsupportedColumnTypeFinding"/> rests on: a SQL_VARIANT column
/// participating in a columnstore index does not deploy, ever, regardless of whether any query
/// references it. Real DDL execution against the standing Docker instance, not a documentation
/// claim taken on faith.
/// </summary>
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
}
