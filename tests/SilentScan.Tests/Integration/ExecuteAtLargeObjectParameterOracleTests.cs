using System.Diagnostics;
using Microsoft.Data.SqlClient;
using SilentScan.Core.Catalog;
using SilentScan.Core.Parsing;
using SilentScan.Core.Predicates;
using SilentScan.Verify;
using SilentScan.Verify.Deployment;

namespace SilentScan.Tests.Integration;

[Trait("Category", "Oracle")]
public sealed class ExecuteAtLargeObjectParameterOracleTests : IAsyncLifetime
{
    private const string ContainerName = "silentscan-sql2025";
    private static readonly SqlServerOptions Options = new(
        Host: "localhost",
        Port: int.TryParse(Environment.GetEnvironmentVariable("SILENTSCAN_SQL2025_PORT"), out var port) ? port : 14331,
        UserId: "sa",
        Password: Environment.GetEnvironmentVariable("SILENTSCAN_SA_PASSWORD") ?? "SilentScan!Dev2026");

    private readonly string _databaseName = $"{nameof(ExecuteAtLargeObjectParameterOracleTests)}_{Guid.NewGuid():N}";
    private readonly string _linkedServerName = $"SS_LOOPBACK_{Guid.NewGuid():N}";

    public async Task InitializeAsync()
    {
        await new DatabaseProvisioner(Options).CreateFreshAsync(_databaseName);

        await using var connection = new SqlConnection(Options.BuildConnectionString(_databaseName));
        await connection.OpenAsync();

        await ExecuteNonQueryAsync(connection,
            $"EXEC sp_addlinkedserver @server = N'{_linkedServerName}', @srvproduct = N'', @provider = N'MSOLEDBSQL19', " +
            "@datasrc = N'localhost', @provstr = N'Encrypt=no;TrustServerCertificate=yes';");
        await ExecuteNonQueryAsync(connection,
            $"EXEC sp_addlinkedsrvlogin @rmtsrvname = N'{_linkedServerName}', @useself = N'FALSE', @locallogin = NULL, " +
            $"@rmtuser = N'{Options.UserId}', @rmtpassword = N'{Options.Password}';");
        await ExecuteNonQueryAsync(connection, $"EXEC sp_serveroption @server = N'{_linkedServerName}', @optname = 'rpc', @optvalue = 'true';");
        await ExecuteNonQueryAsync(connection, $"EXEC sp_serveroption @server = N'{_linkedServerName}', @optname = 'rpc out', @optvalue = 'true';");
    }

    public async Task DisposeAsync()
    {
        await using (var connection = new SqlConnection(Options.BuildConnectionString(_databaseName)))
        {
            await connection.OpenAsync();
            await ExecuteNonQueryAsync(connection, $"EXEC sp_dropserver @server = N'{_linkedServerName}', @droplogins = 'droplogins';");
        }

        await new DatabaseProvisioner(Options).DropIfExistsAsync(_databaseName);
    }

    private static async Task ExecuteNonQueryAsync(SqlConnection connection, string sql)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync();
    }

    private static IReadOnlyList<ExecuteAtLargeObjectParameterFinding> Scan(string sql)
    {
        var result = SqlScriptParser.ParseText("test.sql", sql);
        Assert.False(result.HasErrors, string.Join("; ", result.Errors.Select(e => e.Message)));
        return ExecuteAtLargeObjectParameterScanner.Scan(result, new DatabaseCatalog());
    }

    private static async Task<(int ExitCode, string Output)> RunSqlCmdAsync(string database, string sql)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = "docker",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        startInfo.ArgumentList.Add("exec");
        startInfo.ArgumentList.Add(ContainerName);
        startInfo.ArgumentList.Add("/opt/mssql-tools18/bin/sqlcmd");
        startInfo.ArgumentList.Add("-C");
        startInfo.ArgumentList.Add("-S");
        startInfo.ArgumentList.Add("localhost");
        startInfo.ArgumentList.Add("-U");
        startInfo.ArgumentList.Add(Options.UserId);
        startInfo.ArgumentList.Add("-P");
        startInfo.ArgumentList.Add(Options.Password);
        startInfo.ArgumentList.Add("-d");
        startInfo.ArgumentList.Add(database);
        startInfo.ArgumentList.Add("-Q");
        startInfo.ArgumentList.Add(sql);

        using var process = Process.Start(startInfo)!;
        var stdout = await process.StandardOutput.ReadToEndAsync();
        var stderr = await process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();

        return (process.ExitCode, stdout + stderr);
    }

    [Theory]
    [InlineData("NVARCHAR(MAX)", "N'hello'")]
    [InlineData("VARBINARY(MAX)", "0x0102")]
    public async Task MaxTypedParameter_KillsTheConnectionWithAnAssertionFailure_AndScannerFlagsIt(string dataType, string valueLiteral)
    {
        var sql = $"""
            DECLARE @p {dataType} = {valueLiteral};
            EXEC ('SELECT 1 AS x', @p) AT {_linkedServerName};
            """;

        var (exitCode, output) = await RunSqlCmdAsync(_databaseName, sql);

        Assert.NotEqual(0, exitCode);
        Assert.Contains("assertion", output, StringComparison.OrdinalIgnoreCase);

        var finding = Assert.Single(Scan(sql));
        Assert.Equal(ExecuteAtLargeObjectParameterFindingKind.CrashesSession, finding.Kind);
    }

    [Fact]
    public async Task FixedLengthNVarCharParameter_SucceedsAndScannerDoesNotFlagIt()
    {
        var sql = $"""
            DECLARE @p NVARCHAR(100) = N'hello';
            EXEC ('SELECT 1 AS x', @p) AT {_linkedServerName};
            """;

        await using var connection = new SqlConnection(Options.BuildConnectionString(_databaseName));
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync();

        Assert.Empty(Scan(sql));
    }

    [Fact]
    public async Task XmlParameter_FailsWithMsg9512_AndScannerFlagsIt()
    {
        var sql = $"""
            DECLARE @p XML = '<a/>';
            EXEC ('SELECT 1 AS x', @p) AT {_linkedServerName};
            """;

        await using var connection = new SqlConnection(Options.BuildConnectionString(_databaseName));
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = sql;

        var exception = await Assert.ThrowsAsync<SqlException>(() => command.ExecuteNonQueryAsync());
        Assert.Equal(9512, exception.Number);

        var finding = Assert.Single(Scan(sql));
        Assert.Equal(ExecuteAtLargeObjectParameterFindingKind.XmlRejected, finding.Kind);
    }
}
