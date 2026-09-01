using Microsoft.Data.SqlClient;
using SilentScan.Core.Parsing;
using SilentScan.Core.Predicates;
using SilentScan.Tests.Support;

namespace SilentScan.Tests.Predicates;

[Trait("Category", "Oracle")]
public sealed class WaitForScannerTransactionScopeOracleTests : OracleTestFixture
{
    protected override string DatabaseNameSeed => nameof(WaitForScannerTransactionScopeOracleTests);

    protected override string Ddl => string.Empty;

    private static IReadOnlyList<WaitForFinding> Scan(string sql)
    {
        var result = SqlScriptParser.ParseText("test.sql", sql);
        Assert.False(result.HasErrors, string.Join("; ", result.Errors.Select(e => e.Message)));
        return WaitForScanner.Scan(result);
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

    [Fact]
    public async Task BeginTransactionInEarlierBatch_StaysOpenAcrossTheNextBatch_ThroughAWaitFor()
    {
        await using var connection = await OpenConnectionAsync();

        Assert.Equal(0, await ReadTranCountAsync(connection));

        await new SqlCommand("BEGIN TRANSACTION;", connection).ExecuteNonQueryAsync();

        Assert.Equal(1, await ReadTranCountAsync(connection));

        await new SqlCommand("WAITFOR DELAY '00:00:01';", connection).ExecuteNonQueryAsync();

        Assert.Equal(1, await ReadTranCountAsync(connection));

        await new SqlCommand("ROLLBACK TRANSACTION;", connection).ExecuteNonQueryAsync();

        Assert.Equal(0, await ReadTranCountAsync(connection));

        var findings = Scan(
            """
            BEGIN TRANSACTION;
            GO
            SELECT @@TRANCOUNT;
            WAITFOR DELAY '00:00:01';
            SELECT @@TRANCOUNT;
            ROLLBACK;
            GO
            """);

        var finding = Assert.Single(findings);
        Assert.True(finding.IsInsideTransaction);
    }

    [Fact]
    public async Task TransactionCommittedInEarlierBatch_LeavesLaterBatchWithNoOpenTransaction()
    {
        await using var connection = await OpenConnectionAsync();

        await new SqlCommand("BEGIN TRANSACTION;", connection).ExecuteNonQueryAsync();
        await new SqlCommand("COMMIT TRANSACTION;", connection).ExecuteNonQueryAsync();

        Assert.Equal(0, await ReadTranCountAsync(connection));

        await new SqlCommand("WAITFOR DELAY '00:00:01';", connection).ExecuteNonQueryAsync();

        Assert.Equal(0, await ReadTranCountAsync(connection));

        var findings = Scan(
            """
            BEGIN TRANSACTION;
            COMMIT TRANSACTION;
            GO
            WAITFOR DELAY '00:00:01';
            GO
            """);

        var finding = Assert.Single(findings);
        Assert.False(finding.IsInsideTransaction);
    }

    [Fact]
    public async Task NoPrecedingTransactionInAnyEarlierBatch_LeavesLaterBatchWithNoOpenTransaction()
    {
        await using var connection = await OpenConnectionAsync();

        await new SqlCommand("SELECT 1;", connection).ExecuteNonQueryAsync();

        Assert.Equal(0, await ReadTranCountAsync(connection));

        await new SqlCommand("WAITFOR DELAY '00:00:01';", connection).ExecuteNonQueryAsync();

        Assert.Equal(0, await ReadTranCountAsync(connection));

        var findings = Scan(
            """
            SELECT 1;
            GO
            WAITFOR DELAY '00:00:01';
            GO
            """);

        var finding = Assert.Single(findings);
        Assert.False(finding.IsInsideTransaction);
    }

    [Fact]
    public async Task ProcedureBoundary_DoesNotInheritAnOpenTransactionFromAPrecedingUnrelatedProcedure()
    {
        var findings = Scan(
            """
            CREATE PROCEDURE dbo.ProcA AS
            BEGIN
                BEGIN TRANSACTION;
            END
            GO
            CREATE PROCEDURE dbo.ProcB AS
            BEGIN
                WAITFOR DELAY '00:00:01';
            END
            """);

        var finding = Assert.Single(findings);
        Assert.False(finding.IsInsideTransaction);
    }
}
