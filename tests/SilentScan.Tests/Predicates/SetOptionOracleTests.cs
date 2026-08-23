using SilentScan.Tests.Support;
using SilentScan.Verify.Deployment;
using SilentScan.Verify.Oracle;

namespace SilentScan.Tests.Predicates;

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
    public async Task AnsiNullsOff_FilteredIndexBecomesUnusable()
    {
        var planXml = await new PlanXmlCapture(Options).CaptureAsync(DatabaseName, Probe, ["SET ANSI_NULLS OFF;"]);

        Assert.Contains("PhysicalOp=\"Table Scan\"", planXml);
        Assert.DoesNotContain("PhysicalOp=\"Index Seek\"", planXml);
    }

    [Fact]
    public async Task AnsiWarningsOff_FilteredIndexBecomesUnusable()
    {
        var planXml = await new PlanXmlCapture(Options).CaptureAsync(DatabaseName, Probe, ["SET ANSI_WARNINGS OFF;"]);

        Assert.Contains("PhysicalOp=\"Table Scan\"", planXml);
        Assert.DoesNotContain("PhysicalOp=\"Index Seek\"", planXml);
    }

    [Fact]
    public async Task ConcatNullYieldsNullOff_FilteredIndexBecomesUnusable()
    {
        var planXml = await new PlanXmlCapture(Options).CaptureAsync(DatabaseName, Probe, ["SET CONCAT_NULL_YIELDS_NULL OFF;"]);

        Assert.Contains("PhysicalOp=\"Table Scan\"", planXml);
        Assert.DoesNotContain("PhysicalOp=\"Index Seek\"", planXml);
    }

    [Fact]
    public async Task ArithAbortOff_FilteredIndexRemainsUsable_ConfirmingWhyItIsExcluded()
    {

        var planXml = await new PlanXmlCapture(Options).CaptureAsync(DatabaseName, Probe, ["SET ARITHABORT OFF;"]);

        Assert.Contains("PhysicalOp=\"Index Seek\"", planXml);
        Assert.Contains("IndexKind=\"NonClustered\"", planXml);
    }
}
