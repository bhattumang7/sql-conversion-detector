using Microsoft.Data.SqlClient;
using SilentScan.Tests.Support;

namespace SilentScan.Tests.Predicates;

[Trait("Category", "Oracle")]
public sealed class ForcedParameterizationStaticCallArgumentOracleTests : OracleTestFixture
{
    protected override string DatabaseNameSeed => nameof(ForcedParameterizationStaticCallArgumentOracleTests);

    protected override string Ddl => """
        CREATE TABLE dbo.StaticCallProbeTable (Id INT NOT NULL, Val INT NOT NULL);
        """;

    public override async Task InitializeAsync()
    {
        await base.InitializeAsync();

        var connectionString = Options.BuildConnectionString(DatabaseName);
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync();

        await using var alterParameterization = connection.CreateCommand();
        alterParameterization.CommandText = $"ALTER DATABASE [{DatabaseName}] SET PARAMETERIZATION FORCED;";
        await alterParameterization.ExecuteNonQueryAsync();
    }

    private async Task<string> RunAndCaptureParameterizedPlanTextAsync(string probeSql, string tableLikePattern)
    {
        var connectionString = Options.BuildConnectionString(DatabaseName);
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync();

        await using (var probe = connection.CreateCommand())
        {
            probe.CommandText = probeSql;
            await probe.ExecuteNonQueryAsync();
        }

        await using var readCachedText = connection.CreateCommand();
        readCachedText.CommandText = """
            SELECT st.text
            FROM sys.dm_exec_cached_plans cp
            CROSS APPLY sys.dm_exec_sql_text(cp.plan_handle) st
            WHERE st.dbid = DB_ID()
              AND st.text LIKE @tablePattern
              AND st.text LIKE @parameterizedPattern
              AND st.text NOT LIKE @selfExclusionPattern;
            """;
        readCachedText.Parameters.AddWithValue("@tablePattern", tableLikePattern);
        readCachedText.Parameters.AddWithValue("@parameterizedPattern", "%(@%");
        readCachedText.Parameters.AddWithValue("@selfExclusionPattern", "%dm_exec_cached_plans%");
        await using var reader = await readCachedText.ExecuteReaderAsync();

        Assert.True(await reader.ReadAsync(), "Expected a normalized/parameterized plan-cache entry for the probe statement - none was found.");
        return reader.GetString(0);
    }

    [Fact]
    public async Task CheckSumLiteralArgument_Parameterizes_WhileSiblingWhereClauseLiteralAlsoParameterizes()
    {
        var cachedText = await RunAndCaptureParameterizedPlanTextAsync(
            "SELECT Id FROM dbo.StaticCallProbeTable WHERE Val > 22 AND CHECKSUM('LitArgX') = 0;",
            "%StaticCallProbeTable%");
        var normalized = cachedText.Replace(" ", string.Empty, StringComparison.Ordinal);

        Assert.Contains("Val>@", normalized, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("CHECKSUM(@", normalized, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("LitArgX", cachedText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task DoubleColonStaticCallLiteralArgument_Parameterizes_WhileSiblingWhereClauseLiteralAlsoParameterizes()
    {
        var cachedText = await RunAndCaptureParameterizedPlanTextAsync(
            "SELECT Id FROM dbo.StaticCallProbeTable WHERE Val > 55 AND geography::Parse('POINT(1 1)').STAsText() = 'x';",
            "%StaticCallProbeTable%");
        var normalized = cachedText.Replace(" ", string.Empty, StringComparison.Ordinal);

        Assert.Contains("Val>@", normalized, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("geography::Parse(@", normalized, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("POINT(1 1)", cachedText, StringComparison.Ordinal);
    }
}
