using Microsoft.Data.SqlClient;
using SilentScan.Tests.Support;

namespace SilentScan.Tests.Predicates;

/// <summary>
/// docs/detection-checklist.md "Small precise adds", "Explicit-length audit of CAST/CONVERT to a
/// string type" - a runtime DML/query-result behavior, not a query-plan one, the same discipline
/// <see cref="UnderLengthParameterOracleTests"/> already uses. Proves the underlying mechanism
/// this whole item rests on: an unsized <c>CAST</c>/<c>CONVERT</c> to a string/binary-family type
/// silently truncates to 30 characters, a materially different default than a bare DECLARE's own
/// length-1 default (<see cref="UnderLengthParameterOracleTests"/>'s own subject) - both real,
/// both silent, but different numbers, confirmed directly rather than assumed from documentation.
/// This is a general confirmation of the mechanism, not a per-finding proof - the actual finding
/// path is exercised structurally in <c>TypedPredicateExtractorTests</c>'s
/// <c>Extract_ColumnComparedToUnsizedConvert_*</c> cases, sharing
/// <see cref="UnderLengthParameterFinding"/>/<see cref="OversizedParameterFinding"/>'s existing
/// comparison and reporting path rather than a new finding type, per the checklist item's own
/// instruction.
/// </summary>
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
        // The real, end-to-end consequence: a column wide enough to hold the full value never
        // matches a predicate that routes the comparison value through an unsized CONVERT first,
        // because the CONVERT itself truncates the value to 30 characters before the comparison
        // ever runs - independent of the column's own declared width.
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
