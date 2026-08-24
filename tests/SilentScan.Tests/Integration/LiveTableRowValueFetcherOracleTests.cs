using Microsoft.Data.SqlClient;
using SilentScan.Live.Catalog;
using SilentScan.Tests.Support;

namespace SilentScan.Tests.Integration;

[Trait("Category", "Oracle")]
public sealed class LiveTableRowValueFetcherOracleTests : OracleTestFixture
{
    protected override string DatabaseNameSeed => nameof(LiveTableRowValueFetcherOracleTests);

    protected override string Ddl => """
        CREATE TABLE dbo.Widget (
            Id INT IDENTITY(1,1) PRIMARY KEY,
            GroupId INT NOT NULL,
            Region VARCHAR(10) NULL,
            Code VARCHAR(50) NULL
        );
        """;

    [Fact]
    public async Task TryFetchDistinctValues_FiltersByEqualityKeyAndDeduplicatesValues()
    {
        await using var connection = await OpenConnectionAsync();
        await InsertAsync(connection, 1, null, "Alpha");
        await InsertAsync(connection, 1, null, "Alpha");
        await InsertAsync(connection, 1, null, "Beta");
        await InsertAsync(connection, 2, null, "Gamma");

        var fetcher = new LiveTableRowValueFetcher(connection);

        var result = fetcher.TryFetchDistinctValues("dbo.Widget", "Code", [("GroupId", "1")], maxRows: 10);

        Assert.NotNull(result);
        Assert.Equal(["Alpha", "Beta"], result.OrderBy(v => v, StringComparer.Ordinal));
    }

    [Fact]
    public async Task TryFetchDistinctValues_MaxRowsCapsTheReturnedValueCount()
    {
        await using var connection = await OpenConnectionAsync();
        for (var i = 0; i < 5; i++)
        {
            await InsertAsync(connection, 1, null, $"Value{i}");
        }

        var fetcher = new LiveTableRowValueFetcher(connection);

        var result = fetcher.TryFetchDistinctValues("dbo.Widget", "Code", [("GroupId", "1")], maxRows: 2);

        Assert.NotNull(result);
        Assert.Equal(2, result.Count);
    }

    [Fact]
    public async Task TryFetchDistinctValues_NoRowsMatchTheEqualityKey_ReturnsNull()
    {
        await using var connection = await OpenConnectionAsync();
        await InsertAsync(connection, 1, null, "Alpha");

        var fetcher = new LiveTableRowValueFetcher(connection);

        var result = fetcher.TryFetchDistinctValues("dbo.Widget", "Code", [("GroupId", "999")], maxRows: 10);

        Assert.Null(result);
    }

    [Fact]
    public async Task TryFetchDistinctValues_NullColumnValuesAreExcludedFromTheResult()
    {
        await using var connection = await OpenConnectionAsync();
        await InsertAsync(connection, 1, null, null);
        await InsertAsync(connection, 1, null, "Value");

        var fetcher = new LiveTableRowValueFetcher(connection);

        var result = fetcher.TryFetchDistinctValues("dbo.Widget", "Code", [("GroupId", "1")], maxRows: 10);

        Assert.NotNull(result);
        var single = Assert.Single(result);
        Assert.Equal("Value", single);
    }

    [Fact]
    public async Task TryFetchDistinctValues_MultipleEqualityKeysAreCombinedWithAnd()
    {
        await using var connection = await OpenConnectionAsync();
        await InsertAsync(connection, 1, "A", "MatchesBothKeys");
        await InsertAsync(connection, 1, "B", "MatchesOnlyGroupId");

        var fetcher = new LiveTableRowValueFetcher(connection);

        var result = fetcher.TryFetchDistinctValues(
            "dbo.Widget", "Code", [("GroupId", "1"), ("Region", "A")], maxRows: 10);

        Assert.NotNull(result);
        var single = Assert.Single(result);
        Assert.Equal("MatchesBothKeys", single);
    }

    [Fact]
    public async Task TryFetchDistinctValues_ResultsAreCachedPerCallSignature()
    {
        await using var connection = await OpenConnectionAsync();
        await InsertAsync(connection, 1, null, "First");

        var fetcher = new LiveTableRowValueFetcher(connection);

        var firstCall = fetcher.TryFetchDistinctValues("dbo.Widget", "Code", [("GroupId", "1")], maxRows: 10);
        await InsertAsync(connection, 1, null, "Second");
        var secondCall = fetcher.TryFetchDistinctValues("dbo.Widget", "Code", [("GroupId", "1")], maxRows: 10);

        Assert.NotNull(firstCall);
        Assert.NotNull(secondCall);
        Assert.Equal(firstCall, secondCall);
        Assert.DoesNotContain("Second", secondCall);
    }

    [Fact]
    public async Task TryFetchDistinctValues_QueryAgainstANonexistentTable_ReturnsNullInsteadOfThrowing()
    {
        await using var connection = await OpenConnectionAsync();
        var fetcher = new LiveTableRowValueFetcher(connection);

        var result = fetcher.TryFetchDistinctValues("dbo.NoSuchTableForThisTest", "Code", [], maxRows: 10);

        Assert.Null(result);
    }

    private async Task<SqlConnection> OpenConnectionAsync()
    {
        var connection = new SqlConnection(Options.BuildConnectionString(DatabaseName));
        await connection.OpenAsync();
        return connection;
    }

    private static async Task InsertAsync(SqlConnection connection, int groupId, string? region, string? code)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "INSERT INTO dbo.Widget (GroupId, Region, Code) VALUES (@groupId, @region, @code);";
        command.Parameters.AddWithValue("@groupId", groupId);
        command.Parameters.AddWithValue("@region", (object?)region ?? DBNull.Value);
        command.Parameters.AddWithValue("@code", (object?)code ?? DBNull.Value);
        await command.ExecuteNonQueryAsync();
    }
}
