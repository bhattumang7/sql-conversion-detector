using Microsoft.Data.SqlClient;
using SilentScan.Verify;
using SilentScan.Verify.Deployment;

namespace SilentScan.Tests.Predicates;

[Trait("Category", "Oracle")]
public sealed class StringConcatNullOracleTests : IAsyncLifetime
{
    private static readonly SqlServerOptions Options = new(
        Host: "localhost",
        Port: int.TryParse(Environment.GetEnvironmentVariable("SILENTSCAN_SQL2025_PORT"), out var port) ? port : 14331,
        UserId: "sa",
        Password: Environment.GetEnvironmentVariable("SILENTSCAN_SA_PASSWORD") ?? "SilentScan!Dev2026");

    private readonly string _databaseName = $"{nameof(StringConcatNullOracleTests)}_{Guid.NewGuid():N}";

    public async Task InitializeAsync()
    {
        await new DatabaseProvisioner(Options).CreateFreshAsync(_databaseName);
        await new ScriptDeployer(Options).DeployAsync("ALTER DATABASE CURRENT SET COMPATIBILITY_LEVEL = 170;", _databaseName);
    }

    public async Task DisposeAsync() => await new DatabaseProvisioner(Options).DropIfExistsAsync(_databaseName);

    [Fact]
    public async Task CompatLevel170_ConcatNullYieldsNullOff_PlusOperatorTreatsNullAsEmptyString()
    {
        await using var connection = new SqlConnection(Options.BuildConnectionString(_databaseName));
        await connection.OpenAsync();

        await using (var setCommand = new SqlCommand("SET CONCAT_NULL_YIELDS_NULL OFF;", connection))
        {
            await setCommand.ExecuteNonQueryAsync();
        }

        await using var probeCommand = new SqlCommand("SELECT 'a' + CAST(NULL AS VARCHAR(1));", connection);
        var result = await probeCommand.ExecuteScalarAsync();

        Assert.Equal("a", result);
    }

    [Fact]
    public async Task CompatLevel170_ConcatNullYieldsNullDefaultOn_PlusOperatorPropagatesNull()
    {
        await using var connection = new SqlConnection(Options.BuildConnectionString(_databaseName));
        await connection.OpenAsync();

        await using var probeCommand = new SqlCommand("SELECT 'a' + CAST(NULL AS VARCHAR(1));", connection);
        var result = await probeCommand.ExecuteScalarAsync();

        Assert.Equal(DBNull.Value, result);
    }

    [Fact]
    public async Task CompatLevel170_ConcatNullYieldsNullOff_PersistsAcrossSeparateBatchOnSameSession()
    {
        await using var connection = new SqlConnection(Options.BuildConnectionString(_databaseName));
        await connection.OpenAsync();

        await using (var setCommand = new SqlCommand("SET CONCAT_NULL_YIELDS_NULL OFF;", connection))
        {
            await setCommand.ExecuteNonQueryAsync();
        }

        await using (var probeCommand = new SqlCommand("SELECT 'a' + CAST(NULL AS VARCHAR(1));", connection))
        {
            var firstBatchResult = await probeCommand.ExecuteScalarAsync();
            Assert.Equal("a", firstBatchResult);
        }

        await using (var probeCommand = new SqlCommand("SELECT 'b' + CAST(NULL AS VARCHAR(1));", connection))
        {
            var secondBatchResult = await probeCommand.ExecuteScalarAsync();
            Assert.Equal("b", secondBatchResult);
        }
    }
}
