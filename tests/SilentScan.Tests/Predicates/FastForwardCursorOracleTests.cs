using Microsoft.Data.SqlClient;
using SilentScan.Tests.Support;

namespace SilentScan.Tests.Predicates;

/// <summary>
/// docs/detection-checklist.md Tier 2 "Forced-serial construct inventory" - oracle-confirms the
/// general mechanism once (not per finding): a cursor declared <c>FAST_FORWARD</c> (or the
/// equivalent bare <c>FORWARD_ONLY READ_ONLY</c> lacking an explicit STATIC/KEYSET/DYNAMIC) forces
/// its own defining query's plan serial
/// (<c>NonParallelPlanReason="NoParallelFastForwardCursor"</c>) - the OPPOSITE of the "always use
/// LOCAL FAST_FORWARD" fetch-overhead advice, confirmed directly rather than assumed from that
/// same advice's own reputation. <c>STATIC</c>/<c>KEYSET</c>/<c>DYNAMIC</c> cursors do NOT trigger
/// this mechanism, also confirmed directly.
/// </summary>
public sealed class FastForwardCursorOracleTests : OracleTestFixture
{
    protected override string DatabaseNameSeed => nameof(FastForwardCursorOracleTests);

    protected override string Ddl => """
        CREATE TABLE dbo.BigTable (Id INT NOT NULL, Grp INT NOT NULL, Val VARCHAR(100) NOT NULL);
        GO
        """;

    public override async Task InitializeAsync()
    {
        await base.InitializeAsync();

        await using var connection = new SqlConnection(Options.BuildConnectionString(DatabaseName));
        await connection.OpenAsync();

        await using var seedCommand = new SqlCommand(
            """
            INSERT INTO dbo.BigTable (Id, Grp, Val)
            SELECT TOP (200000) ROW_NUMBER() OVER (ORDER BY (SELECT NULL)),
                   ROW_NUMBER() OVER (ORDER BY (SELECT NULL)) % 100, REPLICATE('x', 50)
            FROM sys.all_objects a CROSS JOIN sys.all_objects b;
            UPDATE STATISTICS dbo.BigTable WITH FULLSCAN;
            """, connection);
        await seedCommand.ExecuteNonQueryAsync();
    }

    private async Task<string> CaptureCursorDefiningQueryPlanAsync(string cursorOptions)
    {
        await using var connection = new SqlConnection(Options.BuildConnectionString(DatabaseName));
        await connection.OpenAsync();

        await using (var onCommand = new SqlCommand("SET STATISTICS XML ON;", connection))
        {
            await onCommand.ExecuteNonQueryAsync();
        }

        var probe =
            $"""
            DECLARE c CURSOR {cursorOptions} FOR SELECT Grp, COUNT(*) FROM dbo.BigTable GROUP BY Grp;
            OPEN c;
            FETCH NEXT FROM c;
            CLOSE c;
            DEALLOCATE c;
            """;

        // The batch has several statements (DECLARE CURSOR, OPEN, FETCH, CLOSE, DEALLOCATE), each
        // producing its own STATISTICS XML result set - the cursor's defining-query reason only
        // ever appears on the DECLARE CURSOR statement's own plan, so every plan-bearing result
        // set is concatenated rather than keeping only the last one (unlike the single-DML-
        // statement probes elsewhere in this stream).
        var planXmlBuilder = new System.Text.StringBuilder();
        await using (var probeCommand = new SqlCommand(probe, connection))
        await using (var reader = await probeCommand.ExecuteReaderAsync())
        {
            do
            {
                while (await reader.ReadAsync())
                {
                    if (reader.FieldCount == 1 && reader.GetFieldType(0) == typeof(string))
                    {
                        var value = reader.GetString(0);
                        if (value.Contains("ShowPlanXML", StringComparison.Ordinal))
                        {
                            planXmlBuilder.Append(value);
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

        var planXml = planXmlBuilder.ToString();
        Assert.NotEmpty(planXml);
        return planXml;
    }

    [Fact]
    public async Task FastForward_ForcesSerial()
    {
        var planXml = await CaptureCursorDefiningQueryPlanAsync("FAST_FORWARD");

        Assert.Contains("NonParallelPlanReason=\"NoParallelFastForwardCursor\"", planXml);
    }

    [Fact]
    public async Task BareForwardOnlyReadOnly_ForcesSerial()
    {
        var planXml = await CaptureCursorDefiningQueryPlanAsync("FORWARD_ONLY READ_ONLY");

        Assert.Contains("NonParallelPlanReason=\"NoParallelFastForwardCursor\"", planXml);
    }

    [Fact]
    public async Task LocalStaticForwardOnlyReadOnly_NeverForcesSerial()
    {
        var planXml = await CaptureCursorDefiningQueryPlanAsync("LOCAL STATIC FORWARD_ONLY READ_ONLY");

        Assert.DoesNotContain("NonParallelPlanReason=\"NoParallelFastForwardCursor\"", planXml);
    }

    [Fact]
    public async Task Dynamic_NeverForcesSerial()
    {
        var planXml = await CaptureCursorDefiningQueryPlanAsync("DYNAMIC");

        Assert.DoesNotContain("NonParallelPlanReason=\"NoParallelFastForwardCursor\"", planXml);
    }

    [Fact]
    public async Task NoOptions_NeverForcesSerial()
    {
        var planXml = await CaptureCursorDefiningQueryPlanAsync(string.Empty);

        Assert.DoesNotContain("NonParallelPlanReason=\"NoParallelFastForwardCursor\"", planXml);
    }
}
