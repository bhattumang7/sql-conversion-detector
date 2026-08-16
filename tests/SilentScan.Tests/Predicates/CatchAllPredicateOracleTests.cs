using Microsoft.Data.SqlClient;
using SilentScan.Tests.Support;

namespace SilentScan.Tests.Predicates;

/// <summary>
/// docs/detection-checklist.md Tier 2 "Catch-all / kitchen-sink predicates" - oracle-confirms
/// the general mechanism once (not per finding, per this session's own precedent): the
/// "(Col = @p OR @p IS NULL)" idiom forces a scan where a bare equality seeks, and
/// <c>OPTION (RECOMPILE)</c> genuinely restores the seek.
///
/// <b>Load-bearing correction to the plan's own flagged uncertainty:</b> <c>SET SHOWPLAN_XML</c>
/// (compile-only, used by every other oracle test in this codebase) CANNOT observe
/// <c>OPTION (RECOMPILE)</c>'s benefit at all - probed directly, a compile-only plan for the
/// catch-all shape WITH <c>OPTION (RECOMPILE)</c> still showed a Table Scan, identical to the
/// un-guarded shape, because <c>SHOWPLAN_XML</c> never actually reaches the execution-time moment
/// RECOMPILE's real value-embedding happens - it produces an ESTIMATED plan the same way a
/// normal compile would, regardless of the hint. Only a REAL EXECUTION (<c>SET STATISTICS XML
/// ON</c>, an actual run of a self-authored probe against the disposable Docker instance -
/// CLAUDE.md permits this exact case, never scanned-target code) shows RECOMPILE's real effect:
/// re-probed the identical query this way and the plan correctly showed an Index Seek.
/// This is why these tests execute a self-authored probe rather than using the compile-only
/// <c>PlanXmlCapture</c> every other Tier-1 sargability oracle test in this codebase uses -
/// deliberate and necessary here, not an inconsistency.
/// </summary>
public sealed class CatchAllPredicateOracleTests : OracleTestFixture
{
    protected override string DatabaseNameSeed => nameof(CatchAllPredicateOracleTests);

    protected override string Ddl => """
        CREATE TABLE dbo.Customers (Id INT NOT NULL, Region VARCHAR(20) NOT NULL);
        GO
        CREATE INDEX IX_Customers_Region ON dbo.Customers(Region);
        GO
        """;

    public override async Task InitializeAsync()
    {
        await base.InitializeAsync();

        await using var connection = new SqlConnection(Options.BuildConnectionString(DatabaseName));
        await connection.OpenAsync();

        await using var seedCommand = new SqlCommand(
            """
            INSERT INTO dbo.Customers (Id, Region)
            SELECT TOP (2000) ROW_NUMBER() OVER (ORDER BY (SELECT NULL)),
                   'R' + CAST(ROW_NUMBER() OVER (ORDER BY (SELECT NULL)) AS VARCHAR(10))
            FROM sys.all_objects a CROSS JOIN sys.all_objects b;
            UPDATE STATISTICS dbo.Customers WITH FULLSCAN;
            """, connection);
        await seedCommand.ExecuteNonQueryAsync();
    }

    private async Task<string> CaptureRealExecutionPlanAsync(string probe)
    {
        await using var connection = new SqlConnection(Options.BuildConnectionString(DatabaseName));
        await connection.OpenAsync();

        await using (var onCommand = new SqlCommand("SET STATISTICS XML ON;", connection))
        {
            await onCommand.ExecuteNonQueryAsync();
        }

        string planXml;
        await using (var probeCommand = new SqlCommand(probe, connection))
        await using (var reader = await probeCommand.ExecuteReaderAsync())
        {
            // The plan XML arrives as its own result set alongside the row results - order
            // between them isn't guaranteed by the API, so scan every result set for the one
            // whose single column actually contains the ShowPlanXML document.
            planXml = string.Empty;
            do
            {
                while (await reader.ReadAsync())
                {
                    if (reader.FieldCount == 1 && reader.GetFieldType(0) == typeof(string))
                    {
                        var value = reader.GetString(0);
                        if (value.Contains("ShowPlanXML", StringComparison.Ordinal))
                        {
                            planXml = value;
                        }
                    }
                }
            }
            while (await reader.NextResultAsync());
        }

        await using (var offCommand = new SqlCommand("SET STATISTICS XML OFF;", connection))
        {
            await offCommand.ExecuteNonQueryAsync();
        }

        Assert.NotEmpty(planXml);
        return planXml;
    }

    [Fact]
    public async Task BareEquality_Seeks()
    {
        var planXml = await CaptureRealExecutionPlanAsync(
            "DECLARE @p VARCHAR(20) = 'R5'; SELECT Id FROM dbo.Customers WHERE Region = @p;");

        Assert.Contains("PhysicalOp=\"Index Seek\"", planXml);
    }

    [Fact]
    public async Task CatchAllShape_NoRecompile_ForcesScan()
    {
        var planXml = await CaptureRealExecutionPlanAsync(
            "DECLARE @p VARCHAR(20) = 'R5'; SELECT Id FROM dbo.Customers WHERE (Region = @p OR @p IS NULL);");

        Assert.Contains("PhysicalOp=\"Table Scan\"", planXml);
        Assert.DoesNotContain("PhysicalOp=\"Index Seek\"", planXml);
    }

    [Fact]
    public async Task CatchAllShape_WithOptionRecompile_RestoresTheSeek()
    {
        var planXml = await CaptureRealExecutionPlanAsync(
            "DECLARE @p VARCHAR(20) = 'R5'; SELECT Id FROM dbo.Customers WHERE (Region = @p OR @p IS NULL) OPTION (RECOMPILE);");

        Assert.Contains("PhysicalOp=\"Index Seek\"", planXml);
    }
}
