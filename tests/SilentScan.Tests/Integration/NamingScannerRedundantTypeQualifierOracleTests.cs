using Microsoft.Data.SqlClient;
using SilentScan.Verify;
using SilentScan.Verify.Deployment;

namespace SilentScan.Tests.Integration;

[Trait("Category", "Oracle")]
public sealed class NamingScannerRedundantTypeQualifierOracleTests
{
    private static readonly SqlServerOptions Options = SqlServerOptions.LocalDocker;

    [Fact]
    public async Task UnqualifiedUserDefinedType_ResolvesViaConnectingPrincipalsDefaultSchema_NotUnconditionallyDbo()
    {
        const string deploymentSql = """
            CREATE SCHEMA alt;
            GO
            CREATE TYPE dbo.mytype FROM INT;
            CREATE TYPE alt.mytype FROM VARCHAR(50);
            GO
            CREATE USER TestDefaultSchemaPrincipal WITHOUT LOGIN WITH DEFAULT_SCHEMA = alt;
            GRANT CREATE TABLE TO TestDefaultSchemaPrincipal;
            GRANT ALTER ON SCHEMA::dbo TO TestDefaultSchemaPrincipal;
            GRANT ALTER ON SCHEMA::alt TO TestDefaultSchemaPrincipal;
            GRANT REFERENCES ON TYPE::dbo.mytype TO TestDefaultSchemaPrincipal;
            GRANT REFERENCES ON TYPE::alt.mytype TO TestDefaultSchemaPrincipal;
            """;

        var databaseName = $"SilentScanTest_{Guid.NewGuid():N}";
        var provisioner = new DatabaseProvisioner(Options);
        await provisioner.CreateFreshAsync(databaseName);
        try
        {
            await new ScriptDeployer(Options).DeployAsync(deploymentSql, databaseName);

            await using var connection = new SqlConnection(Options.BuildConnectionString(databaseName));
            await connection.OpenAsync();

            await using (var setupCommand = connection.CreateCommand())
            {
                setupCommand.CommandText = "EXECUTE AS USER = 'TestDefaultSchemaPrincipal'; CREATE TABLE dbo.T (col mytype); REVERT;";
                await setupCommand.ExecuteNonQueryAsync();
            }

            await using var command = connection.CreateCommand();
            command.CommandText = """
                SELECT s.name
                FROM sys.columns c
                JOIN sys.types t ON t.user_type_id = c.user_type_id
                JOIN sys.schemas s ON s.schema_id = t.schema_id
                WHERE c.object_id = OBJECT_ID('dbo.T') AND c.name = 'col';
                """;
            var boundTypeSchema = (string)(await command.ExecuteScalarAsync())!;

            Assert.Equal("alt", boundTypeSchema);
        }
        finally
        {
            await provisioner.DropIfExistsAsync(databaseName);
        }
    }
}
