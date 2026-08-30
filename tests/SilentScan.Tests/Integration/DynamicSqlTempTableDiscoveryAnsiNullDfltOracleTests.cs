using Microsoft.Data.SqlClient;
using SilentScan.Core.Catalog;
using SilentScan.Core.Parsing;
using SilentScan.Tests.Support;
using SilentScan.Verify;
using SilentScan.Verify.Catalog;
using SilentScan.Verify.Deployment;

namespace SilentScan.Tests.Integration;

[Trait("Category", "Oracle")]
public sealed class DynamicSqlTempTableDiscoveryAnsiNullDfltOracleTests
{
    private static readonly SqlServerOptions Options = SqlServerOptions.LocalDocker;

    [Fact]
    public async Task Discover_TempTableCreatedInsideExecStringUnderAnsiNullDfltOffOn_MatchesRealEngineNotNullDefault()
    {
        const string procedureSql = """
            CREATE PROCEDURE dbo.usp_BuildTemp AS
            BEGIN
                EXEC('SET ANSI_NULL_DFLT_OFF ON; CREATE TABLE #t (Col INT); SELECT is_nullable FROM tempdb.sys.columns WHERE object_id = OBJECT_ID(''tempdb..#t'') AND name = ''Col'';');
            END
            """;

        await AssertRealEngineAndDiscoveryAgreeOnNotNullAsync(procedureSql, "dbo.usp_BuildTemp", "#t", "Col");
    }

    [Fact]
    public async Task Discover_TempTableCreatedInsideExecString_InheritsOuterAnsiNullDfltOffOnFromEnclosingBody()
    {
        const string procedureSql = """
            CREATE PROCEDURE dbo.usp_BuildTemp2 AS
            BEGIN
                SET ANSI_NULL_DFLT_OFF ON;
                EXEC('CREATE TABLE #t2 (Col INT); SELECT is_nullable FROM tempdb.sys.columns WHERE object_id = OBJECT_ID(''tempdb..#t2'') AND name = ''Col'';');
            END
            """;

        await AssertRealEngineAndDiscoveryAgreeOnNotNullAsync(procedureSql, "dbo.usp_BuildTemp2", "#t2", "Col");
    }

    private static async Task AssertRealEngineAndDiscoveryAgreeOnNotNullAsync(
        string procedureSql, string procedureQualifiedName, string tempTableName, string columnName)
    {
        var databaseName = $"SilentScanTest_{Guid.NewGuid():N}";
        var provisioner = new DatabaseProvisioner(Options);
        await provisioner.CreateFreshAsync(databaseName);
        try
        {
            await new ScriptDeployer(Options).DeployAsync(procedureSql, databaseName);
            var connectionString = Options.BuildConnectionString(databaseName);

            await using (var connection = new SqlConnection(connectionString))
            {
                await connection.OpenAsync();
                await using var command = connection.CreateCommand();
                command.CommandText = $"EXEC {procedureQualifiedName};";
                var realIsNullable = (bool)(await command.ExecuteScalarAsync())!;

                Assert.False(realIsNullable);
            }

            var catalog = await new LiveCatalogReader(connectionString).ReadAsync();
            var moduleResult = await new LiveModuleReader(connectionString).ReadAsync();
            var parseResults = moduleResult.Modules
                .Select(m => SqlScriptParser.ParseText(m.QualifiedName, m.Definition, m.UsesQuotedIdentifier))
                .ToList();

            var discovered = DynamicSqlTempTableDiscovery.Discover(
                parseResults, catalog.DefaultCollation?.Name, catalog.TempdbCollation?.Name, catalog.CompatibilityLevel, catalog);

            var table = discovered.Find(tempTableName, procedureQualifiedName);
            Assert.NotNull(table);
            Assert.False(table.FindColumn(columnName)!.IsNullable);
        }
        finally
        {
            await provisioner.DropIfExistsAsync(databaseName);
        }
    }
}
