using Microsoft.Data.SqlClient;
using SilentScan.Tests.Support;

namespace SilentScan.Tests.Predicates;

[Trait("Category", "Oracle")]
public sealed class WindowFrameScannerOracleTests : OracleTestFixture
{
    protected override string DatabaseNameSeed => nameof(WindowFrameScannerOracleTests);

    protected override string Ddl => """
        CREATE TABLE dbo.WindowFrameProbe (GroupId INT NOT NULL, D INT NOT NULL, Amt INT NOT NULL);
        GO
        INSERT INTO dbo.WindowFrameProbe (GroupId, D, Amt) VALUES (1, 1, 10), (1, 2, 20), (1, 3, 30);
        GO
        """;

    private async Task<SqlConnection> OpenConnectionAsync()
    {
        var connection = new SqlConnection(Options.BuildConnectionString(DatabaseName));
        await connection.OpenAsync();
        return connection;
    }

    [Theory]
    [InlineData("ROW_NUMBER() OVER (PARTITION BY GroupId ORDER BY D ROWS BETWEEN UNBOUNDED PRECEDING AND CURRENT ROW)", "ROW_NUMBER")]
    [InlineData("RANK() OVER (PARTITION BY GroupId ORDER BY D ROWS BETWEEN UNBOUNDED PRECEDING AND CURRENT ROW)", "RANK")]
    [InlineData("DENSE_RANK() OVER (PARTITION BY GroupId ORDER BY D ROWS BETWEEN UNBOUNDED PRECEDING AND CURRENT ROW)", "DENSE_RANK")]
    [InlineData("NTILE(2) OVER (PARTITION BY GroupId ORDER BY D ROWS BETWEEN UNBOUNDED PRECEDING AND CURRENT ROW)", "NTILE")]
    [InlineData("LAG(Amt) OVER (PARTITION BY GroupId ORDER BY D ROWS BETWEEN UNBOUNDED PRECEDING AND CURRENT ROW)", "LAG")]
    [InlineData("LEAD(Amt) OVER (PARTITION BY GroupId ORDER BY D ROWS BETWEEN UNBOUNDED PRECEDING AND CURRENT ROW)", "LEAD")]
    [InlineData("PERCENT_RANK() OVER (PARTITION BY GroupId ORDER BY D ROWS BETWEEN UNBOUNDED PRECEDING AND CURRENT ROW)", "PERCENT_RANK")]
    [InlineData("CUME_DIST() OVER (PARTITION BY GroupId ORDER BY D ROWS BETWEEN UNBOUNDED PRECEDING AND CURRENT ROW)", "CUME_DIST")]
    public async Task ExplicitFrameOnFrameIncapableFunction_FailsToCompile(string expression, string functionName)
    {
        await using var connection = await OpenConnectionAsync();
        await using var command = new SqlCommand($"SELECT {expression} FROM dbo.WindowFrameProbe;", connection);

        var exception = await Assert.ThrowsAsync<SqlException>(() => command.ExecuteReaderAsync());

        Assert.Equal(10752, exception.Number);
        Assert.Contains(functionName, exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task AggregateWithOrderByNoExplicitFrame_ComputesImplicitRangeFrame()
    {
        await using var connection = await OpenConnectionAsync();

        await using var implicitCommand = new SqlCommand(
            "SELECT D, SUM(Amt) OVER (PARTITION BY GroupId ORDER BY D) AS Total FROM dbo.WindowFrameProbe ORDER BY D;", connection);
        await using var implicitReader = await implicitCommand.ExecuteReaderAsync();

        var implicitTotals = new List<int>();
        while (await implicitReader.ReadAsync())
        {
            implicitTotals.Add(implicitReader.GetInt32(1));
        }

        await implicitReader.DisposeAsync();

        await using var explicitCommand = new SqlCommand(
            """
            SELECT D, SUM(Amt) OVER (
                PARTITION BY GroupId ORDER BY D
                RANGE BETWEEN UNBOUNDED PRECEDING AND CURRENT ROW) AS Total
            FROM dbo.WindowFrameProbe ORDER BY D;
            """, connection);
        await using var explicitReader = await explicitCommand.ExecuteReaderAsync();

        var explicitTotals = new List<int>();
        while (await explicitReader.ReadAsync())
        {
            explicitTotals.Add(explicitReader.GetInt32(1));
        }

        Assert.Equal([10, 30, 60], implicitTotals);
        Assert.Equal(implicitTotals, explicitTotals);
    }

    [Fact]
    public async Task FirstValueWithOrderByNoExplicitFrame_ComputesImplicitRangeFrame()
    {
        await using var connection = await OpenConnectionAsync();

        await using var implicitCommand = new SqlCommand(
            "SELECT D, FIRST_VALUE(Amt) OVER (PARTITION BY GroupId ORDER BY D) AS First FROM dbo.WindowFrameProbe ORDER BY D;", connection);
        await using var implicitReader = await implicitCommand.ExecuteReaderAsync();

        var implicitFirsts = new List<int>();
        while (await implicitReader.ReadAsync())
        {
            implicitFirsts.Add(implicitReader.GetInt32(1));
        }

        await implicitReader.DisposeAsync();

        await using var explicitCommand = new SqlCommand(
            """
            SELECT D, FIRST_VALUE(Amt) OVER (
                PARTITION BY GroupId ORDER BY D
                RANGE BETWEEN UNBOUNDED PRECEDING AND CURRENT ROW) AS First
            FROM dbo.WindowFrameProbe ORDER BY D;
            """, connection);
        await using var explicitReader = await explicitCommand.ExecuteReaderAsync();

        var explicitFirsts = new List<int>();
        while (await explicitReader.ReadAsync())
        {
            explicitFirsts.Add(explicitReader.GetInt32(1));
        }

        Assert.Equal([10, 10, 10], implicitFirsts);
        Assert.Equal(implicitFirsts, explicitFirsts);
    }
}
