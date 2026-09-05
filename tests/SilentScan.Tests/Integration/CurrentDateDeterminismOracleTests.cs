using Microsoft.Data.SqlClient;
using SilentScan.Verify;
using SilentScan.Verify.Deployment;

namespace SilentScan.Tests.Integration;

[Trait("Category", "Oracle")]
public sealed class CurrentDateDeterminismOracleTests : IAsyncLifetime
{
    private static readonly SqlServerOptions Options = new(
        Host: "localhost",
        Port: int.TryParse(Environment.GetEnvironmentVariable("SILENTSCAN_SQL2025_PORT"), out var port) ? port : 14331,
        UserId: "sa",
        Password: Environment.GetEnvironmentVariable("SILENTSCAN_SA_PASSWORD") ?? "SilentScan!Dev2026");

    private readonly string _databaseName = $"{nameof(CurrentDateDeterminismOracleTests)}_{Guid.NewGuid():N}";

    public async Task InitializeAsync() =>
        await new DatabaseProvisioner(Options).CreateFreshAsync(_databaseName);

    public async Task DisposeAsync() =>
        await new DatabaseProvisioner(Options).DropIfExistsAsync(_databaseName);

    [Fact]
    public async Task CurrentDate_InPersistedComputedColumn_BlocksWith4936()
    {
        await using var connection = new SqlConnection(Options.BuildConnectionString(_databaseName));
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SET QUOTED_IDENTIFIER ON;
            CREATE TABLE dbo.T (Id INT NOT NULL PRIMARY KEY, Dated AS (CAST(CURRENT_DATE AS VARCHAR(30))) PERSISTED);
            """;

        var exception = await Assert.ThrowsAsync<SqlException>(() => command.ExecuteNonQueryAsync());

        Assert.Equal(4936, exception.Number);
    }
}
