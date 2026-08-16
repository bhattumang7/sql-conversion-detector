using Microsoft.Data.SqlClient;
using SilentScan.Tests.Support;

namespace SilentScan.Tests.Predicates;

/// <summary>
/// docs/detection-checklist.md Tier 1 "Under-length and length-defaulted string declarations" -
/// a runtime DML/query-result behavior, not a query-plan one (like <see
/// cref="TemporalBoundaryPrecisionOracleTests"/>/<see cref="WriteLossOracleTests"/>, not the
/// compile-only SHOWPLAN_XML oracle the implicit-conversion streams use): proves the actual
/// truncation mechanism directly, with a real seeded row and real query execution - an
/// under-length variable's ASSIGNMENT (not the predicate) is where the value is silently cut,
/// so by the time the predicate runs, it is comparing against the truncated text, not the
/// original. This is a general confirmation of the rule's own premise (the same discipline
/// CaseFoldColumnPipelineTests/DateFunctionColumnPipelineTests use to confirm a Tier-1 rule's
/// general mechanism), not a per-finding proof - <see cref="UnderLengthParameterFinding"/> stays
/// non-verdict-bearing and structural for the reasons its own doc comment states.
/// </summary>
public sealed class UnderLengthParameterOracleTests : OracleTestFixture
{
    protected override string DatabaseNameSeed => nameof(UnderLengthParameterOracleTests);

    protected override string Ddl => """
        CREATE TABLE dbo.Customers (Code VARCHAR(20) NOT NULL);
        """;

    [Fact]
    public async Task ShorterVariableAssignedALongerLiteral_SilentlyTruncatesAndExcludesTheRealMatch()
    {
        await using var connection = new SqlConnection(Options.BuildConnectionString(DatabaseName));
        await connection.OpenAsync();

        await using (var insertCommand = new SqlCommand("INSERT INTO dbo.Customers (Code) VALUES ('ABCDEF');", connection))
        {
            await insertCommand.ExecuteNonQueryAsync();
        }

        await using (var truncatedCommand = new SqlCommand(
            "DECLARE @p VARCHAR(3) = 'ABCDEF'; SELECT COUNT(*) FROM dbo.Customers WHERE Code = @p;", connection))
        {
            var truncatedCount = (int)(await truncatedCommand.ExecuteScalarAsync())!;
            Assert.Equal(0, truncatedCount);
        }

        await using (var fullLengthCommand = new SqlCommand(
            "DECLARE @p VARCHAR(20) = 'ABCDEF'; SELECT COUNT(*) FROM dbo.Customers WHERE Code = @p;", connection))
        {
            var fullLengthCount = (int)(await fullLengthCommand.ExecuteScalarAsync())!;
            Assert.Equal(1, fullLengthCount);
        }
    }

    [Fact]
    public async Task ShorterVariableAssignedALikePattern_LosesTheWildcardAndChangesWhatMatches()
    {
        // The pattern-shape-changing case: 'ABC%' is 4 characters - a VARCHAR(3) variable
        // truncates it to 'ABC', silently dropping the wildcard entirely. LIKE 'ABC' (no
        // wildcard) requires an EXACT match, not a prefix match, so a row that should have
        // matched as a prefix is silently excluded.
        await using var connection = new SqlConnection(Options.BuildConnectionString(DatabaseName));
        await connection.OpenAsync();

        await using (var insertCommand = new SqlCommand("INSERT INTO dbo.Customers (Code) VALUES ('ABCDEF');", connection))
        {
            await insertCommand.ExecuteNonQueryAsync();
        }

        await using (var truncatedCommand = new SqlCommand(
            "DECLARE @p VARCHAR(3) = 'ABC%'; SELECT COUNT(*) FROM dbo.Customers WHERE Code LIKE @p;", connection))
        {
            var truncatedCount = (int)(await truncatedCommand.ExecuteScalarAsync())!;
            Assert.Equal(0, truncatedCount);
        }

        await using (var fullLengthCommand = new SqlCommand(
            "DECLARE @p VARCHAR(20) = 'ABC%'; SELECT COUNT(*) FROM dbo.Customers WHERE Code LIKE @p;", connection))
        {
            var fullLengthCount = (int)(await fullLengthCommand.ExecuteScalarAsync())!;
            Assert.Equal(1, fullLengthCount);
        }
    }

    [Fact]
    public async Task VariableWithNoExplicitLength_DefaultsToOneAndTruncatesJustAsSeverely()
    {
        await using var connection = new SqlConnection(Options.BuildConnectionString(DatabaseName));
        await connection.OpenAsync();

        await using (var insertCommand = new SqlCommand("INSERT INTO dbo.Customers (Code) VALUES ('ABCDEF');", connection))
        {
            await insertCommand.ExecuteNonQueryAsync();
        }

        await using (var implicitDefaultCommand = new SqlCommand(
            "DECLARE @p VARCHAR = 'ABCDEF'; SELECT COUNT(*) FROM dbo.Customers WHERE Code = @p;", connection))
        {
            var implicitDefaultCount = (int)(await implicitDefaultCommand.ExecuteScalarAsync())!;
            Assert.Equal(0, implicitDefaultCount);
        }
    }
}
