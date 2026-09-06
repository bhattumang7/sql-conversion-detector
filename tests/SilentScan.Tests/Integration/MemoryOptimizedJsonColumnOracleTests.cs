using Microsoft.Data.SqlClient;
using SilentScan.Core.Catalog;
using SilentScan.Core.Parsing;
using SilentScan.Core.Predicates;
using SilentScan.Verify;
using SilentScan.Verify.Deployment;

namespace SilentScan.Tests.Integration;

[Trait("Category", "Oracle")]
public sealed class MemoryOptimizedJsonColumnOracleTests : IAsyncLifetime
{
    private static readonly SqlServerOptions Options = new(
        Host: "localhost",
        Port: int.TryParse(Environment.GetEnvironmentVariable("SILENTSCAN_SQL2025_PORT"), out var port) ? port : 14331,
        UserId: "sa",
        Password: Environment.GetEnvironmentVariable("SILENTSCAN_SA_PASSWORD") ?? "SilentScan!Dev2026");

    private readonly string _databaseName = $"{nameof(MemoryOptimizedJsonColumnOracleTests)}_{Guid.NewGuid():N}";

    public async Task InitializeAsync() => await new DatabaseProvisioner(Options).CreateFreshAsync(_databaseName);

    public async Task DisposeAsync() => await new DatabaseProvisioner(Options).DropIfExistsAsync(_databaseName);

    private static IReadOnlyList<MemoryOptimizedUnsupportedColumnTypeFinding> Scan(string sql)
    {
        var result = SqlScriptParser.ParseText("test.sql", sql);
        Assert.False(result.HasErrors, string.Join("; ", result.Errors.Select(e => e.Message)));
        var catalog = CatalogBuilder.Build([result]);
        return MemoryOptimizedUnsupportedColumnTypeScanner.Scan(catalog);
    }

    [Fact]
    public async Task JsonColumn_OnMemoryOptimizedTable_FailsToDeployWithMsg10794_AndScannerFlagsIt()
    {
        const string EnableMemoryOptimized = """
            DECLARE @dataDir NVARCHAR(260) = (
                SELECT LEFT(physical_name, LEN(physical_name) - CHARINDEX('/', REVERSE(physical_name)) + 1)
                FROM sys.master_files WHERE database_id = DB_ID() AND file_id = 1);
            DECLARE @sql NVARCHAR(MAX) = N'
                ALTER DATABASE CURRENT ADD FILEGROUP MemoryOptimizedFg CONTAINS MEMORY_OPTIMIZED_DATA;
                ALTER DATABASE CURRENT ADD FILE (name=''MemoryOptimizedFile'', filename=''' + @dataDir + N'memopt_json_oracle'') TO FILEGROUP MemoryOptimizedFg;';
            EXEC sp_executesql @sql;
            """;
        await new ScriptDeployer(Options).DeployAsync(EnableMemoryOptimized, _databaseName);

        const string Sql = "CREATE TABLE dbo.Widgets (Id INT NOT NULL PRIMARY KEY NONCLUSTERED, Tag JSON NULL) WITH (MEMORY_OPTIMIZED = ON);";

        await using var connection = new SqlConnection(Options.BuildConnectionString(_databaseName));
        await connection.OpenAsync();
        await using var command = new SqlCommand(Sql, connection);
        var exception = await Assert.ThrowsAsync<SqlException>(() => command.ExecuteNonQueryAsync());
        Assert.Equal(10794, exception.Number);

        var finding = Assert.Single(Scan(Sql));
        Assert.Equal("dbo.Widgets", finding.TableQualifiedName);
        Assert.Equal("Tag", finding.ColumnName);
    }
}
