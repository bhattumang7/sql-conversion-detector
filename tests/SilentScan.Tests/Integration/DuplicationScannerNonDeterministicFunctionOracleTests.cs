using Microsoft.Data.SqlClient;
using SilentScan.Verify;

namespace SilentScan.Tests.Integration;

[Trait("Category", "Oracle")]
public sealed class DuplicationScannerNonDeterministicFunctionOracleTests
{
    private readonly SqlServerOptions _options = SqlServerOptions.LocalDocker;

    [Fact]
    public async Task NewIdComparedToNewIdAcrossMillionsOfRows_NeverMatchesButChecksumOfSameValueAlwaysDoes()
    {
        await using var connection = new SqlConnection(_options.BuildConnectionString());
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT
                COUNT(*) AS total_rows,
                SUM(CASE WHEN NEWID() = NEWID() THEN 1 ELSE 0 END) AS newid_equal_count,
                SUM(CASE WHEN CHECKSUM(a1.object_id) = CHECKSUM(a1.object_id) THEN 1 ELSE 0 END) AS checksum_equal_count
            FROM sys.all_objects a1
            CROSS JOIN sys.all_objects a2;
            """;
        await using var reader = await command.ExecuteReaderAsync();
        await reader.ReadAsync();

        var totalRows = (int)reader["total_rows"];
        var newIdEqualCount = (int)reader["newid_equal_count"];
        var checksumEqualCount = (int)reader["checksum_equal_count"];

        Assert.True(totalRows > 1_000_000, $"expected a multi-million row cross join, got {totalRows}");
        Assert.Equal(0, newIdEqualCount);
        Assert.Equal(totalRows, checksumEqualCount);
    }

    [Fact]
    public async Task RandMinusRandWithinOneStatement_NeverProducesZeroButAlwaysProducesTheSameNonZeroValue()
    {
        await using var connection = new SqlConnection(_options.BuildConnectionString());
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT
                COUNT(*) AS total_rows,
                SUM(CASE WHEN RAND() - RAND() = 0 THEN 1 ELSE 0 END) AS zero_diff_count,
                COUNT(DISTINCT diff) AS distinct_diff_count
            FROM (
                SELECT TOP (5000) RAND() - RAND() AS diff
                FROM sys.all_objects a1
                CROSS JOIN sys.all_objects a2
            ) x;
            """;
        await using var reader = await command.ExecuteReaderAsync();
        await reader.ReadAsync();

        var totalRows = (int)reader["total_rows"];
        var zeroDiffCount = (int)reader["zero_diff_count"];
        var distinctDiffCount = (int)reader["distinct_diff_count"];

        Assert.Equal(5000, totalRows);
        Assert.Equal(0, zeroDiffCount);
        Assert.True(distinctDiffCount >= 1, "expected at least one distinct RAND() - RAND() value");
    }
}
