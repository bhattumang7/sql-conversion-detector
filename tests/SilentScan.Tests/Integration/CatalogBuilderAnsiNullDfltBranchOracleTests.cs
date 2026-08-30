using Microsoft.Data.SqlClient;
using SilentScan.Core.Catalog;
using SilentScan.Core.Parsing;
using SilentScan.Tests.Support;
using SilentScan.Verify;
using SilentScan.Verify.Catalog;
using SilentScan.Verify.Deployment;

namespace SilentScan.Tests.Integration;

[Trait("Category", "Oracle")]
public sealed class CatalogBuilderAnsiNullDfltBranchOracleTests
{
    private static readonly SqlServerOptions Options = SqlServerOptions.LocalDocker;

    [Fact]
    public async Task Build_AnsiNullDfltOffInsideNeverTakenIfBranch_MatchesRealEngineNotTakingTheBranch()
    {
        const string procedureSql = """
            CREATE PROCEDURE dbo.usp_NeverTakenBranch AS
            BEGIN
                IF 1 = 0
                BEGIN
                    SET ANSI_NULL_DFLT_OFF ON;
                END
                CREATE TABLE #t (Col INT);
                SELECT is_nullable FROM tempdb.sys.columns WHERE object_id = OBJECT_ID('tempdb..#t') AND name = 'Col';
            END
            """;

        await AssertModuleBodyStaticAnalysisMatchesRealEngineAsync(procedureSql, "dbo.usp_NeverTakenBranch");
    }

    [Fact]
    public async Task Build_AnsiNullDfltOffInsideNeverRunWhileBody_MatchesRealEngineZeroIterations()
    {
        const string procedureSql = """
            CREATE PROCEDURE dbo.usp_WhileNeverRuns AS
            BEGIN
                WHILE 1 = 0
                BEGIN
                    SET ANSI_NULL_DFLT_OFF ON;
                END
                CREATE TABLE #t (Col INT);
                SELECT is_nullable FROM tempdb.sys.columns WHERE object_id = OBJECT_ID('tempdb..#t') AND name = 'Col';
            END
            """;

        await AssertModuleBodyStaticAnalysisMatchesRealEngineAsync(procedureSql, "dbo.usp_WhileNeverRuns");
    }

    [Fact]
    public async Task Build_AmbientAnsiNullDfltBeforeCreateProcedure_DoesNotLeakIntoProcedureBodyOnLaterExecution()
    {
        const string deploymentSql = """
            SET ANSI_NULL_DFLT_OFF ON;
            GO
            CREATE PROCEDURE dbo.usp_AmbientSetBeforeDefinition AS
            BEGIN
                CREATE TABLE #t (Col INT);
                SELECT is_nullable FROM tempdb.sys.columns WHERE object_id = OBJECT_ID('tempdb..#t') AND name = 'Col';
            END
            """;

        var databaseName = $"SilentScanTest_{Guid.NewGuid():N}";
        var provisioner = new DatabaseProvisioner(Options);
        await provisioner.CreateFreshAsync(databaseName);
        try
        {
            await new ScriptDeployer(Options).DeployAsync(deploymentSql, databaseName);
            var connectionString = Options.BuildConnectionString(databaseName);

            bool realIsNullable;
            await using (var connection = new SqlConnection(connectionString))
            {
                await connection.OpenAsync();
                await using var command = connection.CreateCommand();
                command.CommandText = "EXEC dbo.usp_AmbientSetBeforeDefinition;";
                realIsNullable = (bool)(await command.ExecuteScalarAsync())!;
            }

            Assert.True(realIsNullable);

            var deploymentScriptResult = SqlScriptParser.ParseText("deploy.sql", deploymentSql);
            Assert.False(deploymentScriptResult.HasErrors);

            var catalog = CatalogBuilder.Build([deploymentScriptResult]);
            var table = catalog.Find("#t", "dbo.usp_AmbientSetBeforeDefinition");
            Assert.NotNull(table);
            Assert.Equal(realIsNullable, table.FindColumn("Col")!.IsNullable);
        }
        finally
        {
            await provisioner.DropIfExistsAsync(databaseName);
        }
    }

    private static async Task AssertModuleBodyStaticAnalysisMatchesRealEngineAsync(string procedureSql, string procedureQualifiedName)
    {
        var databaseName = $"SilentScanTest_{Guid.NewGuid():N}";
        var provisioner = new DatabaseProvisioner(Options);
        await provisioner.CreateFreshAsync(databaseName);
        try
        {
            await new ScriptDeployer(Options).DeployAsync(procedureSql, databaseName);
            var connectionString = Options.BuildConnectionString(databaseName);

            bool realIsNullable;
            await using (var connection = new SqlConnection(connectionString))
            {
                await connection.OpenAsync();
                await using var command = connection.CreateCommand();
                command.CommandText = $"EXEC {procedureQualifiedName};";
                realIsNullable = (bool)(await command.ExecuteScalarAsync())!;
            }

            var moduleResult = await new LiveModuleReader(connectionString).ReadAsync();
            var parseResults = moduleResult.Modules
                .Select(m => SqlScriptParser.ParseText(m.QualifiedName, m.Definition, m.UsesQuotedIdentifier))
                .ToList();

            var catalog = CatalogBuilder.Build(parseResults);
            var table = catalog.Find("#t", procedureQualifiedName);
            Assert.NotNull(table);

            var staticIsNullable = table.FindColumn("Col")!.IsNullable;

            Assert.Equal(realIsNullable, staticIsNullable);
        }
        finally
        {
            await provisioner.DropIfExistsAsync(databaseName);
        }
    }
}
