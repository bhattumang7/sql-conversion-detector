using Microsoft.Data.SqlClient;
using SilentScan.Verify;
using SilentScan.Verify.Deployment;

namespace SilentScan.Tests.Integration;

[Trait("Category", "Oracle")]
public sealed class CharToDateConversionDeterminismOracleTests : IAsyncLifetime
{
    private static readonly SqlServerOptions Options = new(
        Host: "localhost",
        Port: int.TryParse(Environment.GetEnvironmentVariable("SILENTSCAN_SQL2025_PORT"), out var port) ? port : 14331,
        UserId: "sa",
        Password: Environment.GetEnvironmentVariable("SILENTSCAN_SA_PASSWORD") ?? "SilentScan!Dev2026");

    private readonly string _databaseName = $"{nameof(CharToDateConversionDeterminismOracleTests)}_{Guid.NewGuid():N}";

    public async Task InitializeAsync() =>
        await new DatabaseProvisioner(Options).CreateFreshAsync(_databaseName);

    public async Task DisposeAsync() =>
        await new DatabaseProvisioner(Options).DropIfExistsAsync(_databaseName);

    [Theory]
    [InlineData("CAST(A AS DATE)")]
    [InlineData("CONVERT(DATE, A)")]
    [InlineData("CONVERT(DATE, A, 0)")]
    [InlineData("CONVERT(DATE, A, 9)")]
    [InlineData("CONVERT(DATE, A, 113)")]
    public async Task CharToDateConversion_WithoutSafeStyle_BlocksWith4936(string expression)
    {
        await using var connection = new SqlConnection(Options.BuildConnectionString(_databaseName));
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = $"""
            SET QUOTED_IDENTIFIER ON;
            CREATE TABLE dbo.T (Id INT NOT NULL PRIMARY KEY, A VARCHAR(20) NULL, B AS ({expression}) PERSISTED);
            """;

        var exception = await Assert.ThrowsAsync<SqlException>(() => command.ExecuteNonQueryAsync());

        Assert.Equal(4936, exception.Number);
    }

    [Theory]
    [InlineData("CONVERT(DATE, A, 101)")]
    [InlineData("CONVERT(DATE, A, 112)")]
    [InlineData("CONVERT(DATE, A, 120)")]
    public async Task CharToDateConversion_WithSafeStyle_PersistsSuccessfully(string expression)
    {
        await using var connection = new SqlConnection(Options.BuildConnectionString(_databaseName));
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = $"""
            SET QUOTED_IDENTIFIER ON;
            CREATE TABLE dbo.T (Id INT NOT NULL PRIMARY KEY, A VARCHAR(20) NULL, B AS ({expression}) PERSISTED);
            """;

        var exception = await Record.ExceptionAsync(() => command.ExecuteNonQueryAsync());

        Assert.Null(exception);
    }

    [Fact]
    public async Task NumericToDateConversion_PersistsSuccessfully()
    {
        await using var connection = new SqlConnection(Options.BuildConnectionString(_databaseName));
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SET QUOTED_IDENTIFIER ON;
            CREATE TABLE dbo.T (Id INT NOT NULL PRIMARY KEY, B AS (CAST(Id AS DATETIME)) PERSISTED);
            """;

        var exception = await Record.ExceptionAsync(() => command.ExecuteNonQueryAsync());

        Assert.Null(exception);
    }
}
