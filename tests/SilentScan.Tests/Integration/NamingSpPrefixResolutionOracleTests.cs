using Microsoft.Data.SqlClient;
using SilentScan.Tests.Support;

namespace SilentScan.Tests.Integration;

[Trait("Category", "Oracle")]
public sealed class NamingSpPrefixResolutionOracleTests : OracleTestFixture
{
    protected override string DatabaseNameSeed => nameof(NamingSpPrefixResolutionOracleTests);

    protected override string Ddl => "SELECT 1;";

    [Fact]
    public async Task LocalRoutineCreatedBeforeMasterRoutine_UnqualifiedCallResolvesToLocal()
    {
        var routineName = $"sp_probe_{Guid.NewGuid():N}";
        await ExecuteAsync(Options.BuildConnectionString(DatabaseName),
            $"CREATE PROCEDURE dbo.{routineName} AS BEGIN SELECT 'local' AS which; END");

        await CreateInMasterAsync(routineName);
        try
        {
            var which = await ExecuteScalarAsync(Options.BuildConnectionString(DatabaseName), $"EXEC {routineName}");

            Assert.Equal("local", which);
        }
        finally
        {
            await DropFromMasterAsync(routineName);
        }
    }

    [Fact]
    public async Task MasterRoutineCreatedBeforeLocalRoutine_UnqualifiedCallStillResolvesToLocal()
    {
        var routineName = $"sp_probe_{Guid.NewGuid():N}";
        await CreateInMasterAsync(routineName);
        try
        {
            await ExecuteAsync(Options.BuildConnectionString(DatabaseName),
                $"CREATE PROCEDURE dbo.{routineName} AS BEGIN SELECT 'local' AS which; END");

            var which = await ExecuteScalarAsync(Options.BuildConnectionString(DatabaseName), $"EXEC {routineName}");

            Assert.Equal("local", which);
        }
        finally
        {
            await DropFromMasterAsync(routineName);
        }
    }

    [Fact]
    public async Task NoLocalRoutine_UnqualifiedCallFallsThroughToMaster()
    {
        var routineName = $"sp_probe_{Guid.NewGuid():N}";
        await CreateInMasterAsync(routineName);
        try
        {
            var which = await ExecuteScalarAsync(Options.BuildConnectionString(DatabaseName), $"EXEC {routineName}");

            Assert.Equal("master", which);
        }
        finally
        {
            await DropFromMasterAsync(routineName);
        }
    }

    private async Task CreateInMasterAsync(string routineName) =>
        await ExecuteAsync(Options.BuildConnectionString("master"),
            $"CREATE PROCEDURE dbo.{routineName} AS BEGIN SELECT 'master' AS which; END");

    private async Task DropFromMasterAsync(string routineName) =>
        await ExecuteAsync(Options.BuildConnectionString("master"),
            $"IF OBJECT_ID('dbo.{routineName}') IS NOT NULL DROP PROCEDURE dbo.{routineName};");

    private static async Task ExecuteAsync(string connectionString, string sql)
    {
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync();
    }

    private static async Task<string> ExecuteScalarAsync(string connectionString, string sql)
    {
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        var result = await command.ExecuteScalarAsync();
        return Assert.IsType<string>(result);
    }
}
