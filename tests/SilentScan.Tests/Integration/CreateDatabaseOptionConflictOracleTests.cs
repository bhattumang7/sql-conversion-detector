using Microsoft.Data.SqlClient;
using SilentScan.Verify;

namespace SilentScan.Tests.Integration;

[Trait("Category", "Oracle")]
public sealed class CreateDatabaseOptionConflictOracleTests
{
    private static readonly SqlServerOptions Options = SqlServerOptions.LocalDocker;

    [Fact]
    public async Task LiveEngine_ContainmentPartialWithCatalogCollation_AlwaysFailsWithMsg12845()
    {
        var databaseName = $"ss_cdoc_{Guid.NewGuid():N}";

        await using var connection = new SqlConnection(Options.BuildConnectionString());
        await connection.OpenAsync();

        try
        {
            await using var command = connection.CreateCommand();
            command.CommandText = $"CREATE DATABASE [{databaseName}] CONTAINMENT = PARTIAL WITH CATALOG_COLLATION = DATABASE_DEFAULT;";

            var exception = await Assert.ThrowsAsync<SqlException>(() => command.ExecuteNonQueryAsync());
            Assert.Equal(12845, exception.Number);
        }
        finally
        {
            await using var cleanup = connection.CreateCommand();
            cleanup.CommandText = $"""
                IF DB_ID(N'{databaseName}') IS NOT NULL
                BEGIN
                    ALTER DATABASE [{databaseName}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE;
                    DROP DATABASE [{databaseName}];
                END
                """;
            await cleanup.ExecuteNonQueryAsync();
        }
    }

    [Fact]
    public async Task LiveEngine_CatalogCollationWithoutContainment_Succeeds()
    {
        var databaseName = $"ss_cdoc_{Guid.NewGuid():N}";

        await using var connection = new SqlConnection(Options.BuildConnectionString());
        await connection.OpenAsync();

        try
        {
            await using var command = connection.CreateCommand();
            command.CommandText = $"CREATE DATABASE [{databaseName}] WITH CATALOG_COLLATION = DATABASE_DEFAULT;";
            await command.ExecuteNonQueryAsync();

            await using var verify = connection.CreateCommand();
            verify.CommandText = $"SELECT DB_ID(N'{databaseName}');";
            Assert.NotEqual(DBNull.Value, await verify.ExecuteScalarAsync());
        }
        finally
        {
            await using var cleanup = connection.CreateCommand();
            cleanup.CommandText = $"""
                IF DB_ID(N'{databaseName}') IS NOT NULL
                BEGIN
                    ALTER DATABASE [{databaseName}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE;
                    DROP DATABASE [{databaseName}];
                END
                """;
            await cleanup.ExecuteNonQueryAsync();
        }
    }
}
