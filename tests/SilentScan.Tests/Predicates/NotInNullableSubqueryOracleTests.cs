using Microsoft.Data.SqlClient;
using SilentScan.Tests.Support;

namespace SilentScan.Tests.Predicates;

/// <summary>
/// docs/detection-checklist.md Tier 2 "NOT IN over a nullable subquery column" - oracle-confirms
/// the general mechanism once (not per finding, per this session's own precedent): a real
/// executed <c>NOT IN (SELECT ...)</c> against a nullable subquery column returns an entirely
/// different (wrong) result set than the same query against a NOT NULL column, given the exact
/// same underlying "which rows should logically match" intent.
///
/// This is a genuine data-correctness bug, not a plan-shape one - the assertion is on the
/// returned ROWS, via real execution, never on plan XML (there is no plan-XML marker for "this
/// predicate silently evaluates to UNKNOWN" - it's a result-set-shape claim, matching how
/// <c>AnsiPaddingMismatchOracleTests</c>/<c>TemporalBoundaryPrecisionOracleTests</c> already
/// verify this class of finding in this codebase).
/// </summary>
[Trait("Category", "Oracle")]
public sealed class NotInNullableSubqueryOracleTests : OracleTestFixture
{
    protected override string DatabaseNameSeed => nameof(NotInNullableSubqueryOracleTests);

    protected override string Ddl => """
        CREATE TABLE dbo.Parent (Id INT NOT NULL);
        GO
        CREATE TABLE dbo.ChildNullable (RefId INT NULL);
        GO
        CREATE TABLE dbo.ChildNotNull (RefId INT NOT NULL);
        GO
        """;

    public override async Task InitializeAsync()
    {
        await base.InitializeAsync();

        await using var connection = new SqlConnection(Options.BuildConnectionString(DatabaseName));
        await connection.OpenAsync();

        await using var seedCommand = new SqlCommand(
            """
            INSERT INTO dbo.Parent (Id) VALUES (1), (2), (3);
            INSERT INTO dbo.ChildNullable (RefId) VALUES (1), (NULL);
            INSERT INTO dbo.ChildNotNull (RefId) VALUES (1);
            """, connection);
        await seedCommand.ExecuteNonQueryAsync();
    }

    private async Task<List<int>> RunAsync(string sql)
    {
        await using var connection = new SqlConnection(Options.BuildConnectionString(DatabaseName));
        await connection.OpenAsync();

        await using var command = new SqlCommand(sql, connection);
        await using var reader = await command.ExecuteReaderAsync();

        var results = new List<int>();
        while (await reader.ReadAsync())
        {
            results.Add(reader.GetInt32(0));
        }

        return results;
    }

    [Fact]
    public async Task NullableSubqueryColumn_NotIn_ReturnsZeroRowsDespiteIntuitivelyMatchingRows()
    {
        var rows = await RunAsync("SELECT Id FROM dbo.Parent WHERE Id NOT IN (SELECT RefId FROM dbo.ChildNullable) ORDER BY Id;");

        // Ids 2 and 3 never appear in ChildNullable, so an ordinary anti-join would return them -
        // the NULL in ChildNullable poisons the whole predicate to UNKNOWN for every row instead.
        Assert.Empty(rows);
    }

    [Fact]
    public async Task NotNullSubqueryColumn_NotIn_ReturnsExpectedAntiJoinRows()
    {
        var rows = await RunAsync("SELECT Id FROM dbo.Parent WHERE Id NOT IN (SELECT RefId FROM dbo.ChildNotNull) ORDER BY Id;");

        Assert.Equal([2, 3], rows);
    }

    [Fact]
    public async Task NullableSubqueryColumn_WithDefensiveNotNullFilter_NotIn_ReturnsExpectedAntiJoinRows()
    {
        var rows = await RunAsync(
            "SELECT Id FROM dbo.Parent WHERE Id NOT IN (SELECT RefId FROM dbo.ChildNullable WHERE RefId IS NOT NULL) ORDER BY Id;");

        Assert.Equal([2, 3], rows);
    }
}
