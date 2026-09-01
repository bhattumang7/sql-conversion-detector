using Microsoft.Data.SqlClient;
using SilentScan.Core.Parsing;
using SilentScan.Core.Predicates;
using SilentScan.Tests.Support;

namespace SilentScan.Tests.Predicates;

[Trait("Category", "Oracle")]
public sealed class CursorCloseOnCommitScannerOracleTests : OracleTestFixture
{
    protected override string DatabaseNameSeed => nameof(CursorCloseOnCommitScannerOracleTests);

    protected override string Ddl => string.Empty;

    private static IReadOnlyList<CursorCloseOnCommitFinding> Scan(string sql)
    {
        var result = SqlScriptParser.ParseText("test.sql", sql);
        Assert.False(result.HasErrors, string.Join("; ", result.Errors.Select(e => e.Message)));
        return CursorCloseOnCommitScanner.Scan(result);
    }

    private async Task<SqlConnection> OpenConnectionAsync()
    {
        var connection = new SqlConnection(Options.BuildConnectionString(DatabaseName));
        await connection.OpenAsync();
        return connection;
    }

    private static async Task<SqlException> ExpectSqlErrorAsync(SqlConnection connection, string sql)
    {
        var command = new SqlCommand(sql, connection);
        return await Assert.ThrowsAsync<SqlException>(() => command.ExecuteNonQueryAsync());
    }

    [Fact]
    public async Task CursorCloseOnCommitOn_FetchAfterCommit_FailsAtRuntimeWithMsg16917_AndScannerFlagsIt()
    {
        await using var connection = await OpenConnectionAsync();

        await new SqlCommand(
            """
            SET CURSOR_CLOSE_ON_COMMIT ON;
            DECLARE cur CURSOR FOR SELECT n FROM (VALUES(1),(2)) AS t(n);
            BEGIN TRANSACTION;
            OPEN cur;
            FETCH NEXT FROM cur;
            COMMIT TRANSACTION;
            """, connection).ExecuteNonQueryAsync();

        var ex = await ExpectSqlErrorAsync(connection, "FETCH NEXT FROM cur;");
        Assert.Equal(16917, ex.Number);

        var findings = Scan(
            """
            SET CURSOR_CLOSE_ON_COMMIT ON;
            DECLARE cur CURSOR FOR SELECT n FROM (VALUES(1),(2)) AS t(n);
            BEGIN TRANSACTION;
            OPEN cur;
            FETCH NEXT FROM cur;
            COMMIT TRANSACTION;
            FETCH NEXT FROM cur;
            """);

        var finding = Assert.Single(findings);
        Assert.Equal("cur", finding.CursorName);
        Assert.False(finding.ClosedByRollback);
        Assert.Equal(7, finding.FetchLine);
    }

    [Fact]
    public async Task CursorCloseOnCommitNotSet_FetchAfterCommit_SucceedsAtRuntime_AndScannerNeverFires()
    {
        await using var connection = await OpenConnectionAsync();

        await new SqlCommand(
            """
            DECLARE cur CURSOR FOR SELECT n FROM (VALUES(1),(2)) AS t(n);
            BEGIN TRANSACTION;
            OPEN cur;
            FETCH NEXT FROM cur;
            COMMIT TRANSACTION;
            """, connection).ExecuteNonQueryAsync();

        var secondFetch = new SqlCommand("FETCH NEXT FROM cur;", connection);
        await secondFetch.ExecuteNonQueryAsync();

        var findings = Scan(
            """
            DECLARE cur CURSOR FOR SELECT n FROM (VALUES(1),(2)) AS t(n);
            BEGIN TRANSACTION;
            OPEN cur;
            FETCH NEXT FROM cur;
            COMMIT TRANSACTION;
            FETCH NEXT FROM cur;
            """);

        Assert.Empty(findings);
    }

    [Fact]
    public async Task CursorCloseOnCommitOn_FetchAfterFullRollback_FailsAtRuntime_AndScannerFlagsRollback()
    {
        await using var connection = await OpenConnectionAsync();

        await new SqlCommand(
            """
            SET CURSOR_CLOSE_ON_COMMIT ON;
            DECLARE cur CURSOR FOR SELECT n FROM (VALUES(1),(2)) AS t(n);
            BEGIN TRANSACTION;
            OPEN cur;
            FETCH NEXT FROM cur;
            ROLLBACK TRANSACTION;
            """, connection).ExecuteNonQueryAsync();

        var ex = await ExpectSqlErrorAsync(connection, "FETCH NEXT FROM cur;");
        Assert.Equal(16917, ex.Number);

        var findings = Scan(
            """
            SET CURSOR_CLOSE_ON_COMMIT ON;
            DECLARE cur CURSOR FOR SELECT n FROM (VALUES(1),(2)) AS t(n);
            BEGIN TRANSACTION;
            OPEN cur;
            FETCH NEXT FROM cur;
            ROLLBACK TRANSACTION;
            FETCH NEXT FROM cur;
            """);

        var finding = Assert.Single(findings);
        Assert.True(finding.ClosedByRollback);
    }

    [Fact]
    public async Task CursorCloseOnCommitOn_ReopenedAfterCommit_FetchSucceeds_AndScannerDoesNotFlag()
    {
        await using var connection = await OpenConnectionAsync();

        await new SqlCommand(
            """
            SET CURSOR_CLOSE_ON_COMMIT ON;
            DECLARE cur CURSOR FOR SELECT n FROM (VALUES(1),(2)) AS t(n);
            BEGIN TRANSACTION;
            OPEN cur;
            FETCH NEXT FROM cur;
            COMMIT TRANSACTION;
            """, connection).ExecuteNonQueryAsync();

        await new SqlCommand("OPEN cur;", connection).ExecuteNonQueryAsync();
        var refetch = new SqlCommand("FETCH NEXT FROM cur;", connection);
        await refetch.ExecuteNonQueryAsync();

        var findings = Scan(
            """
            SET CURSOR_CLOSE_ON_COMMIT ON;
            DECLARE cur CURSOR FOR SELECT n FROM (VALUES(1),(2)) AS t(n);
            BEGIN TRANSACTION;
            OPEN cur;
            FETCH NEXT FROM cur;
            COMMIT TRANSACTION;
            OPEN cur;
            FETCH NEXT FROM cur;
            """);

        Assert.Empty(findings);
    }

    [Fact]
    public async Task CursorCloseOnCommitOn_RollbackToSavepoint_DoesNotCloseCursor_AndScannerDoesNotFlag()
    {
        await using var connection = await OpenConnectionAsync();

        await new SqlCommand(
            """
            SET CURSOR_CLOSE_ON_COMMIT ON;
            DECLARE cur CURSOR FOR SELECT n FROM (VALUES(1),(2)) AS t(n);
            BEGIN TRANSACTION;
            SAVE TRANSACTION sp1;
            OPEN cur;
            FETCH NEXT FROM cur;
            ROLLBACK TRANSACTION sp1;
            """, connection).ExecuteNonQueryAsync();

        var refetch = new SqlCommand("FETCH NEXT FROM cur;", connection);
        await refetch.ExecuteNonQueryAsync();

        var findings = Scan(
            """
            SET CURSOR_CLOSE_ON_COMMIT ON;
            DECLARE cur CURSOR FOR SELECT n FROM (VALUES(1),(2)) AS t(n);
            BEGIN TRANSACTION;
            SAVE TRANSACTION sp1;
            OPEN cur;
            FETCH NEXT FROM cur;
            ROLLBACK TRANSACTION sp1;
            FETCH NEXT FROM cur;
            """);

        Assert.Empty(findings);
    }

    [Fact]
    public async Task CursorCloseOnCommitOn_InnerCommitOfNestedTransaction_DoesNotCloseCursor_OnlyOutermostDoes()
    {
        await using var connection = await OpenConnectionAsync();

        await new SqlCommand(
            """
            SET CURSOR_CLOSE_ON_COMMIT ON;
            DECLARE cur CURSOR FOR SELECT n FROM (VALUES(1),(2),(3)) AS t(n);
            BEGIN TRANSACTION;
            BEGIN TRANSACTION;
            OPEN cur;
            FETCH NEXT FROM cur;
            COMMIT TRANSACTION;
            """, connection).ExecuteNonQueryAsync();

        var innerFetch = new SqlCommand("FETCH NEXT FROM cur;", connection);
        await innerFetch.ExecuteNonQueryAsync();

        await new SqlCommand("COMMIT TRANSACTION;", connection).ExecuteNonQueryAsync();

        var ex = await ExpectSqlErrorAsync(connection, "FETCH NEXT FROM cur;");
        Assert.Equal(16917, ex.Number);

        var findings = Scan(
            """
            SET CURSOR_CLOSE_ON_COMMIT ON;
            DECLARE cur CURSOR FOR SELECT n FROM (VALUES(1),(2),(3)) AS t(n);
            BEGIN TRANSACTION;
            BEGIN TRANSACTION;
            OPEN cur;
            FETCH NEXT FROM cur;
            COMMIT TRANSACTION;
            FETCH NEXT FROM cur;
            COMMIT TRANSACTION;
            FETCH NEXT FROM cur;
            """);

        var finding = Assert.Single(findings);
        Assert.Equal(9, finding.ClosingStatementLine);
        Assert.Equal(10, finding.FetchLine);
    }

    [Fact]
    public async Task CursorCloseOnCommitOn_CursorOpenedInEarlierBatch_CommittedAndFetchedInALaterBatch_StillFlagged()
    {
        await using var connection = await OpenConnectionAsync();

        await new SqlCommand(
            """
            SET CURSOR_CLOSE_ON_COMMIT ON;
            DECLARE cur CURSOR FOR SELECT n FROM (VALUES(1),(2)) AS t(n);
            BEGIN TRANSACTION;
            OPEN cur;
            FETCH NEXT FROM cur;
            """, connection).ExecuteNonQueryAsync();

        await new SqlCommand("COMMIT TRANSACTION;", connection).ExecuteNonQueryAsync();

        var ex = await ExpectSqlErrorAsync(connection, "FETCH NEXT FROM cur;");
        Assert.Equal(16917, ex.Number);

        var findings = Scan(
            """
            SET CURSOR_CLOSE_ON_COMMIT ON;
            DECLARE cur CURSOR FOR SELECT n FROM (VALUES(1),(2)) AS t(n);
            BEGIN TRANSACTION;
            OPEN cur;
            FETCH NEXT FROM cur;
            GO
            COMMIT TRANSACTION;
            GO
            FETCH NEXT FROM cur;
            """);

        var finding = Assert.Single(findings);
        Assert.Equal("cur", finding.CursorName);
        Assert.False(finding.ClosedByRollback);
    }

    [Fact]
    public async Task CursorCloseOnCommitOn_ProcedureBoundary_DoesNotInheritCursorStateFromAnUnrelatedProcedure()
    {
        await using var connection = await OpenConnectionAsync();

        await new SqlCommand(
            """
            SET CURSOR_CLOSE_ON_COMMIT ON;
            DECLARE curA CURSOR FOR SELECT n FROM (VALUES(1)) AS t(n);
            BEGIN TRANSACTION;
            OPEN curA;
            COMMIT TRANSACTION;
            """, connection).ExecuteNonQueryAsync();

        var ex = await ExpectSqlErrorAsync(connection, "FETCH NEXT FROM curA;");
        Assert.Equal(16917, ex.Number);

        var findings = Scan(
            """
            CREATE PROCEDURE dbo.ProcA AS
            BEGIN
                SET CURSOR_CLOSE_ON_COMMIT ON;
                DECLARE curA CURSOR FOR SELECT 1;
                BEGIN TRANSACTION;
                OPEN curA;
                COMMIT TRANSACTION;
            END
            GO
            CREATE PROCEDURE dbo.ProcB AS
            BEGIN
                DECLARE curA CURSOR FOR SELECT 1;
                FETCH NEXT FROM curA;
            END
            """);

        Assert.Empty(findings);
    }
}
