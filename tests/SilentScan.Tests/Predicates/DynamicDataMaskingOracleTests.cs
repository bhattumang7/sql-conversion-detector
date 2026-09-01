using Microsoft.Data.SqlClient;
using SilentScan.Tests.Support;

namespace SilentScan.Tests.Predicates;

[Trait("Category", "Oracle")]
public sealed class DynamicDataMaskingOracleTests : OracleTestFixture
{
    protected override string DatabaseNameSeed => nameof(DynamicDataMaskingOracleTests);

    protected override string Ddl => """
        CREATE TABLE dbo.MaskProbe
        (
            Id  INT NOT NULL PRIMARY KEY,
            Amt INT MASKED WITH (FUNCTION = 'default()') NOT NULL,
            Dt  DATETIME MASKED WITH (FUNCTION = 'default()') NOT NULL
        );
        GO
        INSERT INTO dbo.MaskProbe (Id, Amt, Dt) VALUES (1, 100, '2024-05-01');
        GO
        CREATE USER MaskProbeLowPriv WITHOUT LOGIN;
        GRANT SELECT ON dbo.MaskProbe TO MaskProbeLowPriv;
        GO
        """;

    private async Task<SqlConnection> OpenConnectionAsync()
    {
        var connection = new SqlConnection(Options.BuildConnectionString(DatabaseName));
        await connection.OpenAsync();
        return connection;
    }

    private static async Task<T> RunAsLowPrivAsync<T>(SqlConnection connection, string sql, Func<SqlDataReader, Task<T>> read)
    {
        await using var setContext = new SqlCommand("EXECUTE AS USER = 'MaskProbeLowPriv';", connection);
        await setContext.ExecuteNonQueryAsync();
        try
        {
            await using var command = new SqlCommand(sql, connection);
            await using var reader = await command.ExecuteReaderAsync();
            return await read(reader);
        }
        finally
        {
            await using var revert = new SqlCommand("REVERT;", connection);
            await revert.ExecuteNonQueryAsync();
        }
    }

    [Fact]
    public async Task WhereEqualityOnMaskedColumn_MatchesRealValue_NotDisplayedSentinel()
    {
        await using var connection = await OpenConnectionAsync();

        var matchedRealValue = await RunAsLowPrivAsync(
            connection,
            "SELECT Id FROM dbo.MaskProbe WHERE Amt = 100;",
            async reader => await reader.ReadAsync());

        var matchedDisplayedSentinel = await RunAsLowPrivAsync(
            connection,
            "SELECT Id FROM dbo.MaskProbe WHERE Amt = 0;",
            async reader => await reader.ReadAsync());

        Assert.True(matchedRealValue);
        Assert.False(matchedDisplayedSentinel);
    }

    [Fact]
    public async Task SelectMaskedColumnDirectly_ShowsSentinelNotRealValue()
    {
        await using var connection = await OpenConnectionAsync();

        var amt = await RunAsLowPrivAsync(
            connection,
            "SELECT Amt FROM dbo.MaskProbe WHERE Id = 1;",
            async reader =>
            {
                await reader.ReadAsync();
                return reader.GetInt32(0);
            });

        Assert.Equal(0, amt);
    }

    [Fact]
    public async Task ArithmeticOverMaskedColumn_CollapsesToTypeSentinel_NotARealComputation()
    {
        await using var connection = await OpenConnectionAsync();

        var (amtPlusOne, dtPlusOneDay) = await RunAsLowPrivAsync(
            connection,
            "SELECT Amt + 1, DATEADD(day, 1, Dt) FROM dbo.MaskProbe WHERE Id = 1;",
            async reader =>
            {
                await reader.ReadAsync();
                return (reader.GetInt32(0), reader.GetDateTime(1));
            });

        Assert.Equal(0, amtPlusOne);
        Assert.Equal(new DateTime(1900, 1, 1), dtPlusOneDay);
    }

    [Fact]
    public async Task GroupByMaskedColumn_GroupsByRealValue_DespiteIdenticalDisplayedSentinel()
    {
        await using var connection = await OpenConnectionAsync();

        await using (var insertMore = new SqlCommand(
            "INSERT INTO dbo.MaskProbe (Id, Amt, Dt) VALUES (2, 200, '2024-06-01'), (3, 100, '2024-05-01');", connection))
        {
            await insertMore.ExecuteNonQueryAsync();
        }

        var groupCount = await RunAsLowPrivAsync(
            connection,
            "SELECT Amt, COUNT(*) FROM dbo.MaskProbe GROUP BY Amt;",
            async reader =>
            {
                var count = 0;
                while (await reader.ReadAsync())
                {
                    count++;
                }

                return count;
            });

        Assert.Equal(2, groupCount);
    }
}
