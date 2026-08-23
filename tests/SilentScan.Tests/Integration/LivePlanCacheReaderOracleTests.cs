using Microsoft.Data.SqlClient;
using SilentScan.Live.Catalog;
using SilentScan.Tests.Support;

namespace SilentScan.Tests.Integration;

[Trait("Category", "Oracle")]
public sealed class LivePlanCacheReaderOracleTests : OracleTestFixture
{
    protected override string DatabaseNameSeed => nameof(LivePlanCacheReaderOracleTests);

    protected override string Ddl => """
        CREATE TABLE dbo.Orders (OrderId INT NOT NULL PRIMARY KEY, OrderCode VARCHAR(30) NOT NULL);
        """;

    public override async Task InitializeAsync()
    {
        await base.InitializeAsync();

        var connectionString = Options.BuildConnectionString(DatabaseName);
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync();

        await using (var alterCompat = connection.CreateCommand())
        {
            alterCompat.CommandText = $"ALTER DATABASE [{DatabaseName}] SET COMPATIBILITY_LEVEL = 100;";
            await alterCompat.ExecuteNonQueryAsync();
        }

        await using (var seedQuery = connection.CreateCommand())
        {
            seedQuery.CommandText = "SELECT OrderId FROM dbo.Orders WHERE OrderCode = 'X';";
            await seedQuery.ExecuteNonQueryAsync();
        }
    }

    [Fact]
    public async Task ReadObservedConversionsAsync_DatabaseAtCompatibilityLevel100_DoesNotFailOnTryConvert()
    {
        var reader = new LivePlanCacheReader(Options.BuildConnectionString(DatabaseName));

        var result = await reader.ReadObservedConversionsAsync();

        Assert.Null(result.UnavailableReason);
        Assert.True(result.PlansInspected > 0);
    }
}
