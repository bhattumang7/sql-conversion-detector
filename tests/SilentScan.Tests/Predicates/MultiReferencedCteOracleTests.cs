using Microsoft.Data.SqlClient;
using SilentScan.Tests.Support;

namespace SilentScan.Tests.Predicates;

/// <summary>
/// docs/detection-checklist.md Tier 2 "Lineage-metric findings" - "Multi-referenced CTE". Oracle-
/// confirms the general mechanism once (not per finding, per this session's own precedent): SQL
/// Server does NOT materialize a plain CTE once and reuse it - each downstream reference
/// independently re-runs the CTE's own defining query. Load-bearing, not a folklore-trusted claim:
/// an earlier stream this session (the FAST_FORWARD cursor finding) found a piece of "everyone
/// knows this" SQL Server behavior to be backwards once actually checked against the oracle, so
/// this claim gets the same direct verification rather than being assumed.
/// </summary>
[Trait("Category", "Oracle")]
public sealed class MultiReferencedCteOracleTests : OracleTestFixture
{
    protected override string DatabaseNameSeed => nameof(MultiReferencedCteOracleTests);

    protected override string Ddl => """
        CREATE TABLE dbo.T (Id INT NOT NULL, Val INT NOT NULL);
        GO
        """;

    public override async Task InitializeAsync()
    {
        await base.InitializeAsync();

        await using var connection = new SqlConnection(Options.BuildConnectionString(DatabaseName));
        await connection.OpenAsync();

        await using var seedCommand = new SqlCommand(
            """
            INSERT INTO dbo.T (Id, Val)
            SELECT TOP (1000) ROW_NUMBER() OVER (ORDER BY (SELECT NULL)), 1
            FROM sys.all_objects a CROSS JOIN sys.all_objects b;
            """, connection);
        await seedCommand.ExecuteNonQueryAsync();
    }

    private async Task<int> CaptureBaseTableScanCountAsync(string probe)
    {
        await using var connection = new SqlConnection(Options.BuildConnectionString(DatabaseName));
        await connection.OpenAsync();

        await using (var onCommand = new SqlCommand("SET STATISTICS IO ON;", connection))
        {
            await onCommand.ExecuteNonQueryAsync();
        }

        // STATISTICS IO's per-table lines arrive as SqlInfoMessage events, not as query results -
        // capture them off the connection's InfoMessage event around the real execution.
        var scanCount = -1;
        connection.InfoMessage += (_, args) =>
        {
            foreach (var line in args.Message.Split('\n'))
            {
                if (line.Contains("Table 'T'", StringComparison.Ordinal))
                {
                    var marker = "Scan count ";
                    var start = line.IndexOf(marker, StringComparison.Ordinal) + marker.Length;
                    var end = line.IndexOf(',', start);
                    scanCount = int.Parse(line[start..end], System.Globalization.CultureInfo.InvariantCulture);
                }
            }
        };

        await using (var probeCommand = new SqlCommand(probe, connection))
        {
            await probeCommand.ExecuteNonQueryAsync();
        }

        await using (var offCommand = new SqlCommand("SET STATISTICS IO OFF;", connection))
        {
            await offCommand.ExecuteNonQueryAsync();
        }

        Assert.True(scanCount >= 0, "expected a 'Table 'T'' STATISTICS IO line to have been captured.");
        return scanCount;
    }

    [Fact]
    public async Task CteReferencedOnce_BaseTableScannedOnce()
    {
        var scanCount = await CaptureBaseTableScanCountAsync(
            "WITH cte AS (SELECT Id, Val FROM dbo.T) SELECT Id FROM cte;");

        Assert.Equal(1, scanCount);
    }

    [Fact]
    public async Task CteReferencedTwice_BaseTableScannedTwice()
    {
        var scanCount = await CaptureBaseTableScanCountAsync(
            "WITH cte AS (SELECT Id, Val FROM dbo.T) SELECT a.Id FROM cte a JOIN cte b ON a.Id = b.Id;");

        Assert.Equal(2, scanCount);
    }
}
