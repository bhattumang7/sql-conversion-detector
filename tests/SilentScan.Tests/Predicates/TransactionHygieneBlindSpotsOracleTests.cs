using Microsoft.Data.SqlClient;
using SilentScan.Core.Parsing;
using SilentScan.Core.Predicates;
using SilentScan.Tests.Support;

namespace SilentScan.Tests.Predicates;

[Trait("Category", "Oracle")]
public sealed class TransactionHygieneBlindSpotsOracleTests : OracleTestFixture
{
    protected override string DatabaseNameSeed => nameof(TransactionHygieneBlindSpotsOracleTests);

    protected override string Ddl => """
        CREATE TABLE dbo.T1 (Id INT NOT NULL PRIMARY KEY);
        GO
        CREATE PROCEDURE dbo.p_savepoint_leak AS
        BEGIN
            BEGIN TRANSACTION;
            SAVE TRANSACTION sp1;
            UPDATE dbo.T1 SET Id = Id;
            ROLLBACK TRANSACTION sp1;
            RETURN;
        END
        GO
        CREATE PROCEDURE dbo.p_savepoint_then_full_rollback AS
        BEGIN
            BEGIN TRANSACTION;
            SAVE TRANSACTION sp1;
            UPDATE dbo.T1 SET Id = Id;
            ROLLBACK TRANSACTION sp1;
            ROLLBACK TRANSACTION;
            RETURN;
        END
        GO
        CREATE PROCEDURE dbo.p_implicit_transaction_leak AS
        BEGIN
            SET IMPLICIT_TRANSACTIONS ON;
            UPDATE dbo.T1 SET Id = Id;
            RETURN;
        END
        GO
        CREATE PROCEDURE dbo.p_implicit_transaction_committed AS
        BEGIN
            SET IMPLICIT_TRANSACTIONS ON;
            UPDATE dbo.T1 SET Id = Id;
            COMMIT TRANSACTION;
            RETURN;
        END
        GO
        CREATE PROCEDURE dbo.p_implicit_transaction_no_dml AS
        BEGIN
            SET IMPLICIT_TRANSACTIONS ON;
            DECLARE @x INT = 1;
            SELECT @x;
            RETURN;
        END
        GO
        CREATE PROCEDURE dbo.p_xact_abort_doomed_commit AS
        BEGIN
            SET XACT_ABORT ON;
            BEGIN TRANSACTION;
            BEGIN TRY
                SELECT 1 / 0;
                COMMIT TRANSACTION;
            END TRY
            BEGIN CATCH
                COMMIT TRANSACTION;
            END CATCH
        END
        GO
        CREATE PROCEDURE dbo.p_xact_abort_correct_rollback AS
        BEGIN
            SET XACT_ABORT ON;
            BEGIN TRANSACTION;
            BEGIN TRY
                SELECT 1 / 0;
                COMMIT TRANSACTION;
            END TRY
            BEGIN CATCH
                ROLLBACK TRANSACTION;
            END CATCH
        END
        GO
        """;

    private static IReadOnlyList<TransactionHygieneFinding> Scan(string procedureBody)
    {
        var sql = $"CREATE PROCEDURE dbo.p AS\nBEGIN\n{procedureBody}\nEND";
        var result = SqlScriptParser.ParseText("test.sql", sql);
        Assert.False(result.HasErrors, string.Join("; ", result.Errors.Select(e => e.Message)));
        return TransactionHygieneScanner.Scan(result);
    }

    private async Task<SqlConnection> OpenConnectionAsync()
    {
        var connection = new SqlConnection(Options.BuildConnectionString(DatabaseName));
        await connection.OpenAsync();
        return connection;
    }

    private static async Task<int> ReadTranCountAsync(SqlConnection connection)
    {
        await using var command = new SqlCommand("SELECT @@TRANCOUNT;", connection);
        return (int)(await command.ExecuteScalarAsync())!;
    }

    private static async Task CleanUpAnyOpenTransactionAsync(SqlConnection connection)
    {
        await using var cleanup = new SqlCommand("IF @@TRANCOUNT > 0 ROLLBACK TRANSACTION;", connection);
        await cleanup.ExecuteNonQueryAsync();
    }

    [Fact]
    public async Task RollbackToSavepoint_DoesNotCloseTheTransaction_EngineLeavesTranCountElevated()
    {
        await using var connection = await OpenConnectionAsync();
        try
        {
            await using var exec = new SqlCommand("EXEC dbo.p_savepoint_leak;", connection);
            var ex = await Assert.ThrowsAsync<SqlException>(() => exec.ExecuteNonQueryAsync());
            Assert.Equal(266, ex.Number);

            Assert.Equal(1, await ReadTranCountAsync(connection));
        }
        finally
        {
            await CleanUpAnyOpenTransactionAsync(connection);
        }
    }

    [Fact]
    public void RollbackToSavepoint_ScannerNowFlagsTheStillOpenTransaction()
    {
        var findings = Scan(
            """
            BEGIN TRANSACTION;
            SAVE TRANSACTION sp1;
            UPDATE dbo.T1 SET Id = Id;
            ROLLBACK TRANSACTION sp1;
            RETURN;
            """);

        var finding = Assert.Single(findings);
        Assert.Equal(TransactionHygieneFindingKind.UnresolvedOnSomePath, finding.Kind);
    }

    [Fact]
    public async Task RollbackToSavepointFollowedByFullRollback_ClosesCleanly_NeverFires()
    {
        await using var connection = await OpenConnectionAsync();

        await using var exec = new SqlCommand("EXEC dbo.p_savepoint_then_full_rollback;", connection);
        await exec.ExecuteNonQueryAsync();

        Assert.Equal(0, await ReadTranCountAsync(connection));

        var findings = Scan(
            """
            BEGIN TRANSACTION;
            SAVE TRANSACTION sp1;
            UPDATE dbo.T1 SET Id = Id;
            ROLLBACK TRANSACTION sp1;
            ROLLBACK TRANSACTION;
            RETURN;
            """);

        Assert.Empty(findings);
    }

    [Fact]
    public async Task ImplicitTransactions_UnclosedUpdate_EngineLeavesTranCountElevatedWithNoBeginTransactionInSourceAndNoWarning()
    {
        await using var connection = await OpenConnectionAsync();
        try
        {
            await using var exec = new SqlCommand("EXEC dbo.p_implicit_transaction_leak;", connection);
            await exec.ExecuteNonQueryAsync();

            Assert.Equal(1, await ReadTranCountAsync(connection));
        }
        finally
        {
            await CleanUpAnyOpenTransactionAsync(connection);
        }
    }

    [Fact]
    public void ImplicitTransactions_ScannerFlagsTheImplicitlyOpenedTransaction()
    {
        var findings = Scan(
            """
            SET IMPLICIT_TRANSACTIONS ON;
            UPDATE dbo.T1 SET Id = Id;
            RETURN;
            """);

        var finding = Assert.Single(findings);
        Assert.Equal(TransactionHygieneFindingKind.ImplicitTransactionUnresolvedOnSomePath, finding.Kind);
    }

    [Fact]
    public async Task ImplicitTransactions_Committed_NeverFires()
    {
        await using var connection = await OpenConnectionAsync();

        await using var exec = new SqlCommand("EXEC dbo.p_implicit_transaction_committed;", connection);
        await exec.ExecuteNonQueryAsync();

        Assert.Equal(0, await ReadTranCountAsync(connection));

        var findings = Scan(
            """
            SET IMPLICIT_TRANSACTIONS ON;
            UPDATE dbo.T1 SET Id = Id;
            COMMIT TRANSACTION;
            RETURN;
            """);

        Assert.Empty(findings);
    }

    [Fact]
    public async Task ImplicitTransactions_NoDmlOnlyDeclareAndFromlessSelect_NeverOpensATransaction_NeverFires()
    {
        await using var connection = await OpenConnectionAsync();

        await using var exec = new SqlCommand("EXEC dbo.p_implicit_transaction_no_dml;", connection);
        await exec.ExecuteNonQueryAsync();

        Assert.Equal(0, await ReadTranCountAsync(connection));

        var findings = Scan(
            """
            SET IMPLICIT_TRANSACTIONS ON;
            DECLARE @x INT = 1;
            SELECT @x;
            RETURN;
            """);

        Assert.Empty(findings);
    }

    [Fact]
    public async Task CommitInCatch_UnderXactAbort_AlwaysFailsWithMsg3930_EngineThenAutoRollsBackTheDoomedTransaction()
    {
        await using var connection = await OpenConnectionAsync();
        try
        {
            await using var exec = new SqlCommand("EXEC dbo.p_xact_abort_doomed_commit;", connection);
            var ex = await Assert.ThrowsAsync<SqlException>(() => exec.ExecuteNonQueryAsync());
            Assert.Equal(3930, ex.Number);

            Assert.Equal(0, await ReadTranCountAsync(connection));
        }
        finally
        {
            await CleanUpAnyOpenTransactionAsync(connection);
        }
    }

    [Fact]
    public void CommitInCatch_UnderXactAbort_ScannerFlagsTheDoomedCommit()
    {
        var findings = Scan(
            """
            SET XACT_ABORT ON;
            BEGIN TRANSACTION;
            BEGIN TRY
                SELECT 1 / 0;
                COMMIT TRANSACTION;
            END TRY
            BEGIN CATCH
                COMMIT TRANSACTION;
            END CATCH
            """);

        var finding = Assert.Single(findings);
        Assert.Equal(TransactionHygieneFindingKind.CommitAfterXactAbortDoomsTransaction, finding.Kind);
    }

    [Fact]
    public async Task RollbackInCatch_UnderXactAbort_SucceedsCleanly_ScannerNeverFlagsIt()
    {
        await using var connection = await OpenConnectionAsync();

        await using var exec = new SqlCommand("EXEC dbo.p_xact_abort_correct_rollback;", connection);
        await exec.ExecuteNonQueryAsync();

        Assert.Equal(0, await ReadTranCountAsync(connection));

        var findings = Scan(
            """
            SET XACT_ABORT ON;
            BEGIN TRANSACTION;
            BEGIN TRY
                SELECT 1 / 0;
                COMMIT TRANSACTION;
            END TRY
            BEGIN CATCH
                ROLLBACK TRANSACTION;
            END CATCH
            """);

        Assert.Empty(findings);
    }

    [Fact]
    public async Task CommitInCatch_WithoutXactAbort_SucceedsBecauseTheTransactionIsNotDoomed_ScannerNeverFlagsIt()
    {
        await using var connection = await OpenConnectionAsync();

        await using var exec = new SqlCommand(
            """
            BEGIN TRANSACTION;
            BEGIN TRY
                SELECT 1 / 0;
                COMMIT TRANSACTION;
            END TRY
            BEGIN CATCH
                SELECT XACT_STATE();
                COMMIT TRANSACTION;
            END CATCH
            """,
            connection);
        await exec.ExecuteNonQueryAsync();

        Assert.Equal(0, await ReadTranCountAsync(connection));

        var findings = Scan(
            """
            BEGIN TRANSACTION;
            BEGIN TRY
                SELECT 1 / 0;
                COMMIT TRANSACTION;
            END TRY
            BEGIN CATCH
                COMMIT TRANSACTION;
            END CATCH
            """);

        Assert.Empty(findings);
    }
}
