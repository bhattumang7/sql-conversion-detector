using Microsoft.Data.SqlClient;
using SilentScan.Core.Catalog;
using SilentScan.Core.Parsing;
using SilentScan.Core.Predicates;
using SilentScan.Tests.Support;

namespace SilentScan.Tests.Integration;

[Trait("Category", "Oracle")]
public sealed class FullTextPredicateInAggregateOracleTests : OracleTestFixture
{
    protected override string DatabaseNameSeed => nameof(FullTextPredicateInAggregateOracleTests);

    protected override string Ddl => """
        CREATE TABLE dbo.Ticket (Id INT NOT NULL CONSTRAINT PK_Ticket PRIMARY KEY, Notes NVARCHAR(4000) NULL);
        GO
        CREATE FULLTEXT CATALOG TicketCatalog AS DEFAULT;
        GO
        CREATE FULLTEXT INDEX ON dbo.Ticket(Notes) KEY INDEX PK_Ticket;
        """;

    private static IReadOnlyList<FullTextPredicateInAggregateFinding> Scan(string sql)
    {
        var result = SqlScriptParser.ParseText("test.sql", sql);
        Assert.False(result.HasErrors, string.Join("; ", result.Errors.Select(e => e.Message)));
        var catalog = CatalogBuilder.Build([result]);
        return FullTextPredicateInAggregateScanner.Scan(result, catalog);
    }

    private async Task<SqlException> ExecuteExpectingFailureAsync(string sql)
    {
        await using var connection = new SqlConnection(Options.BuildConnectionString(DatabaseName));
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        return await Assert.ThrowsAsync<SqlException>(() => command.ExecuteScalarAsync());
    }

    private async Task ExecuteAsync(string sql)
    {
        await using var connection = new SqlConnection(Options.BuildConnectionString(DatabaseName));
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteScalarAsync();
    }

    [Fact]
    public async Task ContainsInsideSumCase_FailsToCompileWithMsg30082_AndScannerFlagsIt()
    {
        const string Sql = """
            SELECT SUM(CASE WHEN CONTAINS(Notes, 'urgent') THEN 1 ELSE 0 END) FROM dbo.Ticket;
            """;

        var exception = await ExecuteExpectingFailureAsync(Sql);
        Assert.Equal(30082, exception.Number);

        var finding = Assert.Single(Scan(Sql));
        Assert.Equal("SUM", finding.AggregateFunctionName);
        Assert.Equal("CONTAINS", finding.FullTextFunctionName);
    }

    [Fact]
    public async Task FreetextInsideCountCase_FailsToCompileWithMsg30082_AndScannerFlagsIt()
    {
        const string Sql = """
            SELECT COUNT(CASE WHEN FREETEXT(Notes, 'urgent') THEN 1 END) FROM dbo.Ticket;
            """;

        var exception = await ExecuteExpectingFailureAsync(Sql);
        Assert.Equal(30082, exception.Number);

        var finding = Assert.Single(Scan(Sql));
        Assert.Equal("COUNT", finding.AggregateFunctionName);
        Assert.Equal("FREETEXT", finding.FullTextFunctionName);
    }

    [Fact]
    public async Task ContainsInWhereClause_DeploysCleanly_AndScannerDoesNotFlagIt()
    {
        const string Sql = """
            SELECT COUNT(*) FROM dbo.Ticket WHERE CONTAINS(Notes, 'urgent');
            """;

        await ExecuteAsync(Sql);

        Assert.Empty(Scan(Sql));
    }

    [Fact]
    public async Task ContainsInsideWindowedAggregate_DeploysCleanly_AndScannerDoesNotFlagIt()
    {
        const string Sql = """
            SELECT SUM(CASE WHEN CONTAINS(Notes, 'urgent') THEN 1 ELSE 0 END) OVER (PARTITION BY Id) FROM dbo.Ticket;
            """;

        await ExecuteAsync(Sql);

        Assert.Empty(Scan(Sql));
    }
}
