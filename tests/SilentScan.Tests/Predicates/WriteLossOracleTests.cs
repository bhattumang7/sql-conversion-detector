using Microsoft.Data.SqlClient;
using SilentScan.Tests.Support;

namespace SilentScan.Tests.Predicates;

[Trait("Category", "Oracle")]
public sealed class WriteLossOracleTests : OracleTestFixture
{
    protected override string DatabaseNameSeed => nameof(WriteLossOracleTests);

    protected override string Ddl => """
        CREATE TABLE dbo.T (
            Id INT IDENTITY PRIMARY KEY,
            DecCol DECIMAL(10,2) NULL,
            IntCol INT NULL,
            DateCol DATE NULL,
            VarCol VARCHAR(20) NULL
        );
        """;

    [Theory]
    [InlineData("INSERT INTO dbo.T (DecCol) VALUES (123.456)", "SELECT CAST(DecCol AS VARCHAR(20)) FROM dbo.T", "123.46")]
    [InlineData("INSERT INTO dbo.T (IntCol) VALUES (7.9)", "SELECT CAST(IntCol AS VARCHAR(20)) FROM dbo.T", "7")]
    [InlineData("INSERT INTO dbo.T (DateCol) VALUES ('2024-01-15 13:45:00')", "SELECT CONVERT(VARCHAR(10), DateCol, 120) FROM dbo.T", "2024-01-15")]
    public async Task Insert_LossyAssignment_SilentlyRoundsOrTruncates_NoError(string insertSql, string selectSql, string expected)
    {
        await using var connection = new SqlConnection(Options.BuildConnectionString(DatabaseName));
        await connection.OpenAsync();

        await using (var insertCommand = new SqlCommand(insertSql, connection))
        {
            await insertCommand.ExecuteNonQueryAsync();
        }

        await using var selectCommand = new SqlCommand(selectSql, connection);
        var result = await selectCommand.ExecuteScalarAsync();
        Assert.Equal(expected, result?.ToString());
    }

    [Fact]
    public async Task Insert_UnicodeCharacterOutsideCodepage_SilentlyReplacedWithQuestionMark_NoError()
    {
        await using var connection = new SqlConnection(Options.BuildConnectionString(DatabaseName));
        await connection.OpenAsync();

        await using (var insertCommand = new SqlCommand("INSERT INTO dbo.T (VarCol) VALUES (N'日本語')", connection))
        {
            await insertCommand.ExecuteNonQueryAsync();
        }

        await using var selectCommand = new SqlCommand("SELECT VarCol, DATALENGTH(VarCol) FROM dbo.T", connection);
        await using var reader = await selectCommand.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        Assert.Equal("???", reader.GetString(0));
        Assert.Equal(3, reader.GetInt32(1));
    }

    [Fact]
    public async Task Insert_TooLongString_RaisesHardError_NotSilent()
    {

        await using var connection = new SqlConnection(Options.BuildConnectionString(DatabaseName));
        await connection.OpenAsync();

        await using var insertCommand = new SqlCommand("INSERT INTO dbo.T (VarCol) VALUES ('123456789012345678901')", connection);
        var exception = await Assert.ThrowsAsync<SqlException>(() => insertCommand.ExecuteNonQueryAsync());
        Assert.Contains("truncated", exception.Message, StringComparison.OrdinalIgnoreCase);
    }
}
