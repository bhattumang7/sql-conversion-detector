using Microsoft.Data.SqlClient;
using SilentScan.Verify;
using SilentScan.Verify.Deployment;

namespace SilentScan.Tests.Integration;

[Trait("Category", "Oracle")]
public sealed class SchemaDependencyTableLevelDefaultConstraintOracleTests
{
    private static readonly SqlServerOptions Options = SqlServerOptions.LocalDocker;

    [Fact]
    public async Task AlterTableAddConstraintDefault_CallingScalarUdf_RunsOnInsert()
    {
        const string ddl = """
            CREATE FUNCTION dbo.fn_Stamp() RETURNS INT AS BEGIN RETURN 42; END;
            GO
            CREATE TABLE dbo.T (Id INT NOT NULL PRIMARY KEY, Code INT NULL);
            GO
            ALTER TABLE dbo.T ADD CONSTRAINT DF_Code DEFAULT dbo.fn_Stamp() FOR Code;
            """;

        var databaseName = $"SilentScanTest_{Guid.NewGuid():N}";
        var provisioner = new DatabaseProvisioner(Options);
        await provisioner.CreateFreshAsync(databaseName);
        try
        {
            await new ScriptDeployer(Options).DeployAsync(ddl, databaseName);

            await using var connection = new SqlConnection(Options.BuildConnectionString(databaseName));
            await connection.OpenAsync();

            await using (var insertCommand = connection.CreateCommand())
            {
                insertCommand.CommandText = "INSERT INTO dbo.T (Id) VALUES (1);";
                await insertCommand.ExecuteNonQueryAsync();
            }

            await using var selectCommand = connection.CreateCommand();
            selectCommand.CommandText = "SELECT Code FROM dbo.T WHERE Id = 1;";
            var code = (int)(await selectCommand.ExecuteScalarAsync())!;

            Assert.Equal(42, code);
        }
        finally
        {
            await provisioner.DropIfExistsAsync(databaseName);
        }
    }
}
