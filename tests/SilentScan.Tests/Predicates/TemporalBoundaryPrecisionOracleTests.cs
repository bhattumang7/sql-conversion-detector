using Microsoft.Data.SqlClient;
using SilentScan.Tests.Support;

namespace SilentScan.Tests.Predicates;

[Trait("Category", "Oracle")]
public sealed class TemporalBoundaryPrecisionOracleTests : OracleTestFixture
{
    protected override string DatabaseNameSeed => nameof(TemporalBoundaryPrecisionOracleTests);

    protected override string Ddl => """
        CREATE TABLE dbo.Events (EventId INT IDENTITY PRIMARY KEY, OccurredAt DATETIME2(7) NOT NULL);
        """;

    [Fact]
    public async Task RowAtPrecisionGapEdge_SilentlyExcludedByThreeDigitBoundary_ButIncludedBySafeRewrite()
    {
        await using var connection = new SqlConnection(Options.BuildConnectionString(DatabaseName));
        await connection.OpenAsync();

        await using (var insertCommand = new SqlCommand(
            "INSERT INTO dbo.Events (OccurredAt) VALUES ('2024-12-31 23:59:59.9999999');", connection))
        {
            await insertCommand.ExecuteNonQueryAsync();
        }

        await using (var buggyCommand = new SqlCommand(
            "SELECT COUNT(*) FROM dbo.Events WHERE OccurredAt BETWEEN '2024-01-01' AND '2024-12-31 23:59:59.997';", connection))
        {
            var buggyCount = (int)(await buggyCommand.ExecuteScalarAsync())!;
            Assert.Equal(0, buggyCount);
        }

        await using (var safeCommand = new SqlCommand(
            "SELECT COUNT(*) FROM dbo.Events WHERE OccurredAt >= '2024-01-01' AND OccurredAt < '2025-01-01';", connection))
        {
            var safeCount = (int)(await safeCommand.ExecuteScalarAsync())!;
            Assert.Equal(1, safeCount);
        }
    }
}
