using SilentScan.Tests.Support;
using SilentScan.Verify.Deployment;
using SilentScan.Verify.Oracle;

namespace SilentScan.Tests.Predicates;

/// <summary>
/// docs/detection-checklist.md Tier 1 "SET options that silently disable plan features" -
/// oracle-confirms the actual mechanism QUOTED_IDENTIFIER OFF/NUMERIC_ROUNDABORT ON block: a
/// filtered index the optimizer would otherwise seek through becomes unusable, falling back to a
/// table/heap scan, compile-only. Uses <see cref="PlanXmlCapture"/>'s <c>sessionSetStatements</c>
/// overload to pin the setting BEFORE compilation on the same connection - still entirely
/// compile-only, SHOWPLAN_XML never returns rows, so this never crosses into executing anything.
///
/// Assertions check <c>PhysicalOp</c>/<c>IndexKind</c> directly rather than a raw substring match
/// for the index's own name - an earlier draft asserted <c>DoesNotContain(indexName, planXml)</c>
/// and was WRONG: the index's name still appears in a plan that never actually uses it (in
/// <c>OptimizerStatsUsage/StatisticsInfo</c>, since its statistics were still loaded and
/// considered), so that assertion always passed regardless of whether the index was really used -
/// caught directly by first testing ARITHABORT OFF this same way and finding it made no real
/// difference to the plan at all (see <see cref="SilentScan.Core.Predicates.SetOptionFinding"/>'s
/// own doc comment on why ARITHABORT was dropped).
/// </summary>
[Trait("Category", "Oracle")]
public sealed class SetOptionOracleTests : OracleTestFixture
{
    protected override string DatabaseNameSeed => nameof(SetOptionOracleTests);

    protected override string Ddl => """
        CREATE TABLE dbo.Orders (OrderId INT NOT NULL, CustomerId INT NOT NULL, IsActive BIT NOT NULL);
        GO
        CREATE INDEX IX_Orders_ActiveCustomer ON dbo.Orders(CustomerId) WHERE IsActive = 1;
        GO
        """;

    private const string Probe = "SELECT CustomerId FROM dbo.Orders WHERE IsActive = 1 AND CustomerId = 5;";

    public override async Task InitializeAsync()
    {
        await base.InitializeAsync();

        // A filtered index over an empty/tiny table can seek trivially regardless of the SET
        // options in play - real population (2,000 rows) plus fresh statistics is what makes the
        // optimizer's own filtered-index-eligibility decision actually observable, the same trap
        // documented elsewhere in this codebase for GetRangeThroughConvert/GetRangeWithMismatchedTypes.
        var seedRows = """
            INSERT INTO dbo.Orders (OrderId, CustomerId, IsActive)
            SELECT TOP (2000) ROW_NUMBER() OVER (ORDER BY (SELECT NULL)), ROW_NUMBER() OVER (ORDER BY (SELECT NULL)), 1
            FROM sys.all_objects a CROSS JOIN sys.all_objects b;
            UPDATE STATISTICS dbo.Orders WITH FULLSCAN;
            """;
        await new ScriptDeployer(Options).DeployAsync(seedRows, DatabaseName);
    }

    [Fact]
    public async Task NumericRoundabortOff_FilteredIndexIsUsable()
    {
        var planXml = await new PlanXmlCapture(Options).CaptureAsync(DatabaseName, Probe, ["SET NUMERIC_ROUNDABORT OFF;"]);

        Assert.Contains("PhysicalOp=\"Index Seek\"", planXml);
        Assert.Contains("IndexKind=\"NonClustered\"", planXml);
    }

    [Fact]
    public async Task NumericRoundabortOn_FilteredIndexBecomesUnusable()
    {
        var planXml = await new PlanXmlCapture(Options).CaptureAsync(DatabaseName, Probe, ["SET NUMERIC_ROUNDABORT ON;"]);

        Assert.Contains("PhysicalOp=\"Table Scan\"", planXml);
        Assert.DoesNotContain("PhysicalOp=\"Index Seek\"", planXml);
    }

    [Fact]
    public async Task QuotedIdentifierOff_FilteredIndexBecomesUnusable()
    {
        var planXml = await new PlanXmlCapture(Options).CaptureAsync(DatabaseName, Probe, ["SET QUOTED_IDENTIFIER OFF;"]);

        Assert.Contains("PhysicalOp=\"Table Scan\"", planXml);
        Assert.DoesNotContain("PhysicalOp=\"Index Seek\"", planXml);
    }

    [Fact]
    public async Task ArithAbortOff_FilteredIndexRemainsUsable_ConfirmingWhyItIsExcluded()
    {
        // Directly re-confirms the oracle finding that made this stream drop the ARITHABORT
        // sub-rule (docs/detection-checklist.md's own correction, SetOptionFinding's doc
        // comment): unlike QUOTED_IDENTIFIER/NUMERIC_ROUNDABORT, ARITHABORT OFF does NOT disable
        // this filtered index on this engine version/edition - the seek survives unchanged. Kept
        // as a permanent regression guard against silently re-adding the false-positive rule.
        var planXml = await new PlanXmlCapture(Options).CaptureAsync(DatabaseName, Probe, ["SET ARITHABORT OFF;"]);

        Assert.Contains("PhysicalOp=\"Index Seek\"", planXml);
        Assert.Contains("IndexKind=\"NonClustered\"", planXml);
    }
}
