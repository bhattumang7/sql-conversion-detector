using SilentScan.Verify;
using SilentScan.Verify.Deployment;
using SilentScan.Verify.Oracle;

namespace SilentScan.Tests.Integration;

/// <summary>
/// Locks in the ExpressionDerivedFinding rule against the real oracle. Unlike the type-
/// precedence findings, a CAST buried in an upstream view is an EXPLICIT conversion, so
/// CONVERT_IMPLICIT never appears in the plan for it - what confirms the finding instead is
/// the absence of an Index Seek on the underlying column's own index, proving the engine
/// really can't use it. Found via a direct question: does silentscan catch an int -&gt; varchar
/// -&gt; int round trip across two views and a proc? It didn't (a real gap, fixed by this rule) -
/// this test is the oracle proof the fix is real, not just theoretically plausible.
/// </summary>
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
        // Control: proves the index is genuinely usable on the real, unwrapped column - so
        // the round-trip case below losing the seek is attributable to the CAST chain, not
        // to something incidental like row count or statistics.
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
