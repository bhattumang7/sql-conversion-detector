using Microsoft.Data.SqlClient;
using SilentScan.Tests.Support;

namespace SilentScan.Tests.Predicates;

[Trait("Category", "Oracle")]
public sealed class TransactionHygieneOracleTests : OracleTestFixture
{
    protected override string DatabaseNameSeed => nameof(TransactionHygieneOracleTests);

    protected override string Ddl => """
        CREATE PROCEDURE dbo.p_leaky AS
        BEGIN
            BEGIN TRANSACTION;
            IF (1 = 1)
            BEGIN
                RETURN;
            END
            COMMIT TRANSACTION;
        END
        GO
        CREATE PROCEDURE dbo.p_catch_no_rollback AS
        BEGIN
            BEGIN TRANSACTION;
            BEGIN TRY
                SELECT 1 / 0;
                COMMIT TRANSACTION;
            END TRY
            BEGIN CATCH
                SELECT ERROR_MESSAGE();
            END CATCH
        END
        GO
        CREATE PROCEDURE dbo.p_well_formed AS
        BEGIN
            BEGIN TRANSACTION;
            BEGIN TRY
                SELECT 1;
                COMMIT TRANSACTION;
            END TRY
            BEGIN CATCH
                IF (@@TRANCOUNT > 0) ROLLBACK TRANSACTION;
            END CATCH
        END
        GO
        """;

    private async Task<SqlConnection> OpenConnectionAsync()
    {
        var connection = new SqlConnection(Options.BuildConnectionString(DatabaseName));
        await connection.OpenAsync();
        return connection;
    }

    private static async Task CleanUpAnyOpenTransactionAsync(SqlConnection connection)
    {
        await using var cleanup = new SqlCommand(
            "IF @@TRANCOUNT > 0 ROLLBACK TRANSACTION;", connection);
        await cleanup.ExecuteNonQueryAsync();
    }

    [Fact]
    public async Task ProcedureReturnsWithoutResolving_LeavesTranCountElevated_AndEngineRaisesMsg266()
    {
        await using var connection = await OpenConnectionAsync();
        try
        {
            await using var before = new SqlCommand("SELECT @@TRANCOUNT;", connection);
            Assert.Equal(0, (int)(await before.ExecuteScalarAsync())!);

            await using var exec = new SqlCommand("EXEC dbo.p_leaky;", connection);
            var ex = await Assert.ThrowsAsync<SqlException>(() => exec.ExecuteNonQueryAsync());
            Assert.Equal(266, ex.Number);

            await using var after = new SqlCommand("SELECT @@TRANCOUNT;", connection);
            Assert.Equal(1, (int)(await after.ExecuteScalarAsync())!);
        }
        finally
        {
            await CleanUpAnyOpenTransactionAsync(connection);
        }
    }

    [Fact]
    public async Task CatchBlockNeverRollsBack_LeavesTranCountElevatedIdenticallyDespiteTheError()
    {
        await using var connection = await OpenConnectionAsync();
        try
        {
            await using var exec = new SqlCommand("EXEC dbo.p_catch_no_rollback;", connection);
            var ex = await Assert.ThrowsAsync<SqlException>(() => exec.ExecuteNonQueryAsync());
            Assert.Equal(266, ex.Number);

            await using var after = new SqlCommand("SELECT @@TRANCOUNT;", connection);
            Assert.Equal(1, (int)(await after.ExecuteScalarAsync())!);
        }
        finally
        {
            await CleanUpAnyOpenTransactionAsync(connection);
        }
    }

    [Fact]
    public async Task WellFormedProcedure_AlwaysResolvesOnEveryPath_NeverLeavesTranCountElevated()
    {
        await using var connection = await OpenConnectionAsync();

        await using var exec = new SqlCommand("EXEC dbo.p_well_formed;", connection);
        await exec.ExecuteNonQueryAsync();

        await using var after = new SqlCommand("SELECT @@TRANCOUNT;", connection);
        Assert.Equal(0, (int)(await after.ExecuteScalarAsync())!);
    }
}
