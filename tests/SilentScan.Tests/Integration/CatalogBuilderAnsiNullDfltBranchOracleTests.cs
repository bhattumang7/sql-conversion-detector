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
    public async Task Build_AnsiNullDfltOffTurnedOff_IsANoOpThatDoesNotRevertToNullable()
    {
        const string procedureSql = """
            CREATE PROCEDURE dbo.usp_OffThenOffOff AS
            BEGIN
                SET ANSI_NULL_DFLT_OFF ON;
                SET ANSI_NULL_DFLT_OFF OFF;
                CREATE TABLE #t (Col INT);
                SELECT is_nullable FROM tempdb.sys.columns WHERE object_id = OBJECT_ID('tempdb..#t') AND name = 'Col';
            END
            """;

        await AssertModuleBodyStaticAnalysisMatchesRealEngineAsync(procedureSql, "dbo.usp_OffThenOffOff");
    }

    [Fact]
    public async Task Build_ComputedColumnUnderAnsiNullDfltOff_MatchesRealEngineIgnoringTheOverride()
    {
        const string deploymentSql = """
            SET ANSI_NULL_DFLT_OFF ON;
            CREATE TABLE dbo.T (A INT NOT NULL, Col AS (A + 1));
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
                command.CommandText = "SELECT is_nullable FROM sys.columns WHERE object_id = OBJECT_ID('dbo.T') AND name = 'Col';";
                realIsNullable = (bool)(await command.ExecuteScalarAsync())!;
            }

            var deploymentScriptResult = SqlScriptParser.ParseText("deploy.sql", deploymentSql);
            Assert.False(deploymentScriptResult.HasErrors);

            var catalog = CatalogBuilder.Build([deploymentScriptResult]);
            var staticIsNullable = catalog.Find("dbo.T")!.FindColumn("Col")!.IsNullable;

            Assert.Equal(realIsNullable, staticIsNullable);
        }
        finally
        {
            await provisioner.DropIfExistsAsync(databaseName);
        }
    }

    [Fact]
    public async Task Build_AlterTableAddColumnUnderAnsiNullDfltOff_MatchesRealEngineIgnoringTheOverride()
    {
        const string deploymentSql = """
            CREATE TABLE dbo.T (A INT NOT NULL);
            GO
            SET ANSI_NULL_DFLT_OFF ON;
            ALTER TABLE dbo.T ADD Col INT;
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
                command.CommandText = "SELECT is_nullable FROM sys.columns WHERE object_id = OBJECT_ID('dbo.T') AND name = 'Col';";
                realIsNullable = (bool)(await command.ExecuteScalarAsync())!;
            }

            var deploymentScriptResult = SqlScriptParser.ParseText("deploy.sql", deploymentSql);
            Assert.False(deploymentScriptResult.HasErrors);

            var catalog = CatalogBuilder.Build([deploymentScriptResult]);
            var staticIsNullable = catalog.Find("dbo.T")!.FindColumn("Col")!.IsNullable;

            Assert.Equal(realIsNullable, staticIsNullable);
        }
        finally
        {
            await provisioner.DropIfExistsAsync(databaseName);
        }
    }

    [Fact]
    public async Task Build_TableVariableColumnUnderAnsiNullDfltOff_MatchesRealEngineIgnoringTheOverride()
    {
        const string deploymentSql = "SET ANSI_NULL_DFLT_OFF ON; DECLARE @t TABLE (Col INT);";

        var connectionString = Options.BuildConnectionString();

        bool realIsNullable;
        await using (var connection = new SqlConnection(connectionString))
        {
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = "SELECT is_nullable FROM sys.dm_exec_describe_first_result_set(@batch, NULL, 0) WHERE name = 'Col';";
            command.Parameters.AddWithValue("@batch", deploymentSql + " SELECT Col FROM @t;");
            realIsNullable = (bool)(await command.ExecuteScalarAsync())!;
        }

        var deploymentScriptResult = SqlScriptParser.ParseText("deploy.sql", deploymentSql);
        Assert.False(deploymentScriptResult.HasErrors);

        var catalog = CatalogBuilder.Build([deploymentScriptResult]);
        var staticIsNullable = catalog.Find("@t")!.FindColumn("Col")!.IsNullable;

        Assert.Equal(realIsNullable, staticIsNullable);
    }

    [Fact]
    public async Task Build_CreateTypeAsTableColumnUnderAnsiNullDfltOff_MatchesRealEngineIgnoringTheOverride()
    {
        const string deploymentSql = """
            SET ANSI_NULL_DFLT_OFF ON;
            CREATE TYPE dbo.T AS TABLE (Col INT);
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
                command.CommandText = """
                    SELECT c.is_nullable FROM sys.columns c
                    JOIN sys.table_types tt ON tt.type_table_object_id = c.object_id
                    WHERE tt.name = 'T' AND c.name = 'Col';
                    """;
                realIsNullable = (bool)(await command.ExecuteScalarAsync())!;
            }

            var deploymentScriptResult = SqlScriptParser.ParseText("deploy.sql", deploymentSql);
            Assert.False(deploymentScriptResult.HasErrors);

            var catalog = CatalogBuilder.Build([deploymentScriptResult]);
            var staticIsNullable = catalog.Find("dbo.T")!.FindColumn("Col")!.IsNullable;

            Assert.Equal(realIsNullable, staticIsNullable);
        }
        finally
        {
            await provisioner.DropIfExistsAsync(databaseName);
        }
    }

    [Fact]
    public async Task Build_MultiStatementTvfReturnColumnUnderAnsiNullDfltOff_MatchesRealEngineIgnoringTheOverride()
    {
        const string deploymentSql = """
            SET ANSI_NULL_DFLT_OFF ON;
            GO
            CREATE FUNCTION dbo.fn_Probe()
            RETURNS @t TABLE (Col INT)
            AS
            BEGIN
                RETURN;
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
                command.CommandText = """
                    SELECT c.is_nullable FROM sys.columns c
                    JOIN sys.objects o ON o.object_id = c.object_id
                    WHERE o.name = 'fn_Probe' AND c.name = 'Col';
                    """;
                realIsNullable = (bool)(await command.ExecuteScalarAsync())!;
            }

            var deploymentScriptResult = SqlScriptParser.ParseText("deploy.sql", deploymentSql);
            Assert.False(deploymentScriptResult.HasErrors);

            var catalog = CatalogBuilder.Build([deploymentScriptResult]);
            var staticIsNullable = catalog.Find("@t", "dbo.fn_Probe")!.FindColumn("Col")!.IsNullable;

            Assert.Equal(realIsNullable, staticIsNullable);
        }
        finally
        {
            await provisioner.DropIfExistsAsync(databaseName);
        }
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
