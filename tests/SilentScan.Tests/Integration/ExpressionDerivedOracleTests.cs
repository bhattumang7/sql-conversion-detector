using SilentScan.Verify;
using SilentScan.Verify.Deployment;
using SilentScan.Verify.Oracle;

namespace SilentScan.Tests.Integration;

[Trait("Category", "Oracle")]
public sealed class ExpressionDerivedOracleTests : IAsyncLifetime
{
    private const string DatabaseName = "SilentScanExpressionDerivedOracleTest";
    private const string IndexName = "IX_Orders_CustomerId";

    private readonly SqlServerOptions _options = SqlServerOptions.LocalDocker;
    private readonly DatabaseProvisioner _provisioner;
    private readonly PlanXmlCapture _planXmlCapture;

    public ExpressionDerivedOracleTests()
    {
        _provisioner = new DatabaseProvisioner(_options);
        _planXmlCapture = new PlanXmlCapture(_options);
    }

    public async Task InitializeAsync()
    {
        await _provisioner.CreateFreshAsync(DatabaseName);
        await new ScriptDeployer(_options).DeployAsync(
            $$"""
            CREATE TABLE dbo.Orders (OrderId INT NOT NULL PRIMARY KEY, CustomerId INT NOT NULL);
            GO
            CREATE INDEX {{IndexName}} ON dbo.Orders(CustomerId);
            GO
            CREATE VIEW dbo.vw_OrdersStr AS
            SELECT OrderId, CAST(CustomerId AS VARCHAR(20)) AS CustomerIdStr
            FROM dbo.Orders;
            GO
            CREATE VIEW dbo.vw_OrdersRoundTrip AS
            SELECT OrderId, CAST(CustomerIdStr AS INT) AS CustomerIdAgain
            FROM dbo.vw_OrdersStr;
            GO
            """,
            DatabaseName);
    }

    public async Task DisposeAsync() =>
        await _provisioner.DropIfExistsAsync(DatabaseName);

    [Fact]
    public async Task DirectQueryOnBaseColumn_UsesIndexSeek()
    {

        var planXml = await _planXmlCapture.CaptureAsync(DatabaseName, "SELECT OrderId FROM dbo.Orders WHERE CustomerId = 5;");

        Assert.True(IndexAccessDetector.HasIndexSeek(planXml, IndexName));
    }

    [Fact]
    public async Task RoundTrippedIntThroughTwoViews_NoIndexSeek()
    {
        var planXml = await _planXmlCapture.CaptureAsync(DatabaseName, "SELECT OrderId FROM dbo.vw_OrdersRoundTrip WHERE CustomerIdAgain = 5;");

        Assert.False(IndexAccessDetector.HasIndexSeek(planXml, IndexName));
    }
}
