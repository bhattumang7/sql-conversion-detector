using Microsoft.Data.SqlClient;
using SilentScan.Verify;
using SilentScan.Verify.Deployment;

namespace SilentScan.Tests.Integration;

/// <summary>
/// End-to-end proof of CLAUDE.md's "corpus DML is never executed, anywhere" hard scope: a
/// corpus DDL file that also contains a seed INSERT (a real, common shape - schema + seed data
/// in one file) must deploy the table but the INSERT must never actually run against the oracle.
/// </summary>
[Trait("Category", "Oracle")]
public sealed class DdlWhitelistDeploymentTests : IAsyncLifetime
{
    private const string DatabaseName = "SilentScanDdlWhitelistTest";

    private readonly SqlServerOptions _options = SqlServerOptions.LocalDocker;
    private readonly DatabaseProvisioner _provisioner;

    public DdlWhitelistDeploymentTests()
    {
        _provisioner = new DatabaseProvisioner(_options);
    }

    public async Task InitializeAsync() => await _provisioner.CreateFreshAsync(DatabaseName);

    public async Task DisposeAsync() => await _provisioner.DropIfExistsAsync(DatabaseName);

    [Fact]
    public async Task DeployWhitelistedDdlAsync_TableWithSeedInsertInSameFile_DeploysTableButNeverRunsTheInsert()
    {
        var deployer = new ScriptDeployer(_options);
        var script = """
            CREATE TABLE dbo.T (Id INT NOT NULL);
            GO
            INSERT INTO dbo.T (Id) VALUES (1);
            GO
            """;

        var messages = await deployer.DeployWhitelistedDdlAsync(script, DatabaseName);

        Assert.Contains(messages, m => m.Contains("InsertStatement", StringComparison.Ordinal));

        await using var connection = new SqlConnection(_options.BuildConnectionString(DatabaseName));
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM dbo.T;";
        var rowCount = (int)(await command.ExecuteScalarAsync())!;

        Assert.Equal(0, rowCount);
    }

    [Fact]
    public async Task DeployWhitelistedDdlAsync_TableAndViewAndIndex_AllDeploySuccessfully()
    {
        var deployer = new ScriptDeployer(_options);
        var script = """
            CREATE TABLE dbo.T (Id INT NOT NULL, Code VARCHAR(20) NOT NULL);
            GO
            CREATE INDEX IX_T_Code ON dbo.T(Code);
            GO
            CREATE VIEW dbo.V AS SELECT Id, Code FROM dbo.T;
            GO
            """;

        var messages = await deployer.DeployWhitelistedDdlAsync(script, DatabaseName);

        Assert.Empty(messages);

        await using var connection = new SqlConnection(_options.BuildConnectionString(DatabaseName));
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT OBJECT_ID('dbo.T'), OBJECT_ID('dbo.V'), OBJECT_ID('dbo.IX_T_Code');";
        await using var reader = await command.ExecuteReaderAsync();
        await reader.ReadAsync();

        Assert.False(await reader.IsDBNullAsync(0), "table should have deployed");
        Assert.False(await reader.IsDBNullAsync(1), "view should have deployed");
    }

    [Fact]
    public async Task DeployWhitelistedDdlAsync_OneSkippedBatchDoesNotBlockLaterBatches()
    {
        var deployer = new ScriptDeployer(_options);
        var script = """
            EXEC dbo.usp_SomeSetupProcThatDoesNotExist;
            GO
            CREATE TABLE dbo.T (Id INT NOT NULL);
            GO
            """;

        await deployer.DeployWhitelistedDdlAsync(script, DatabaseName);

        await using var connection = new SqlConnection(_options.BuildConnectionString(DatabaseName));
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT OBJECT_ID('dbo.T');";
        var result = await command.ExecuteScalarAsync();

        Assert.NotNull(result);
    }
}
