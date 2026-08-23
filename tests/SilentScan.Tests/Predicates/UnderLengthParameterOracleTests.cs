using Microsoft.Data.SqlClient;
using SilentScan.Tests.Support;

namespace SilentScan.Tests.Predicates;

[Trait("Category", "Oracle")]
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
