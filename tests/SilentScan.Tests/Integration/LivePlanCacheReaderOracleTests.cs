using Microsoft.Data.SqlClient;
using SilentScan.Live.Catalog;
using SilentScan.Tests.Support;

namespace SilentScan.Tests.Integration;

/// <summary>
/// Found live against a real database (compatibility level 100, SQL Server 2008): the plan-cache
/// reader's own filter query used TRY_CONVERT (added in compat 110), a hard SQL error on any
/// older-compat database - "not a recognized built-in function name", not a permission gap - that
/// silently degraded the reader to "unavailable" on EVERY pre-2012-compat database regardless of
/// what VIEW SERVER STATE the login actually had. Reproduces that exact compat-level shape against
/// the disposable oracle rather than trusting the fix by inspection alone.
/// </summary>
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

        // A compat level below 110 is the entire point of this test - TRY_CONVERT (this reader's
        // own former query) does not exist there at all.
        await using (var alterCompat = connection.CreateCommand())
        {
            alterCompat.CommandText = $"ALTER DATABASE [{DatabaseName}] SET COMPATIBILITY_LEVEL = 100;";
            await alterCompat.ExecuteNonQueryAsync();
        }

        // Populates a real plan-cache entry for this database so the reader's own FilteredPlans
        // CTE has at least one row to filter - an empty plan cache would let this test pass for
        // the wrong reason (nothing to fail on) rather than because the compat-level fix works.
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
