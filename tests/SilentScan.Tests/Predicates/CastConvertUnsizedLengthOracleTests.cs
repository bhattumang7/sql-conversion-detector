using Microsoft.Data.SqlClient;
using SilentScan.Tests.Support;

namespace SilentScan.Tests.Predicates;

[Trait("Category", "Oracle")]
public sealed class CastConvertUnsizedLengthOracleTests : OracleTestFixture
{
    protected override string DatabaseNameSeed => nameof(CastConvertUnsizedLengthOracleTests);

    protected override string Ddl => """
        CREATE TABLE dbo.Customers (Code VARCHAR(40) NOT NULL);
        """;

    [Fact]
    public async Task UnsizedConvert_TruncatesTo30Characters_NotLength1()
    {
        await using var connection = new SqlConnection(Options.BuildConnectionString(DatabaseName));
        await connection.OpenAsync();

        await using var command = new SqlCommand(
            "SELECT LEN(CONVERT(VARCHAR, REPLICATE('x', 50))) AS convert_len, LEN(CAST(REPLICATE('x', 50) AS VARCHAR)) AS cast_len;",
            connection);
        await using var reader = await command.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());

        Assert.Equal(30, reader.GetInt32(0));
        Assert.Equal(30, reader.GetInt32(1));
    }

    [Fact]
    public async Task UnsizedConvert_AppliesTheSame30CharacterDefault_AcrossEveryStringAndBinaryFamilyType()
    {
        await using var connection = new SqlConnection(Options.BuildConnectionString(DatabaseName));
        await connection.OpenAsync();

        await using var command = new SqlCommand(
            """
            SELECT
                LEN(CONVERT(NVARCHAR, REPLICATE(N'x', 50))) AS nvarchar_len,
                LEN(CONVERT(CHAR, REPLICATE('x', 50))) AS char_len,
                LEN(CONVERT(NCHAR, REPLICATE(N'x', 50))) AS nchar_len,
                DATALENGTH(CONVERT(VARBINARY, REPLICATE(CONVERT(VARBINARY(1), 1), 50))) AS varbinary_len,
                DATALENGTH(CONVERT(BINARY, REPLICATE(CONVERT(VARBINARY(1), 1), 50))) AS binary_len;
            """,
            connection);
        await using var reader = await command.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());

        for (var i = 0; i < 5; i++)
        {
            Assert.Equal(30, reader.GetInt32(i));
        }
    }

    [Fact]
    public async Task ColumnComparedAgainstUnsizedConvertOfALongerValue_SilentlyExcludesTheRealMatch()
    {

        await using var connection = new SqlConnection(Options.BuildConnectionString(DatabaseName));
        await connection.OpenAsync();

        var fullValue = new string('x', 35);

        await using (var insertCommand = new SqlCommand(
            $"INSERT INTO dbo.Customers (Code) VALUES ('{fullValue}');", connection))
        {
            await insertCommand.ExecuteNonQueryAsync();
        }

        await using (var truncatedCommand = new SqlCommand(
            $"DECLARE @x VARCHAR(40) = '{fullValue}'; SELECT COUNT(*) FROM dbo.Customers WHERE Code = CONVERT(VARCHAR, @x);", connection))
        {
            var truncatedCount = (int)(await truncatedCommand.ExecuteScalarAsync())!;
            Assert.Equal(0, truncatedCount);
        }

        await using (var fullLengthCommand = new SqlCommand(
            $"DECLARE @x VARCHAR(40) = '{fullValue}'; SELECT COUNT(*) FROM dbo.Customers WHERE Code = CONVERT(VARCHAR(40), @x);", connection))
        {
            var fullLengthCount = (int)(await fullLengthCommand.ExecuteScalarAsync())!;
            Assert.Equal(1, fullLengthCount);
        }
    }
}
