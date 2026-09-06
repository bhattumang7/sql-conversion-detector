using SilentScan.Verify;
using SilentScan.Verify.Deployment;
using SilentScan.Verify.Oracle;

namespace SilentScan.Tests.Integration;

[Trait("Category", "Oracle")]
public sealed class ColumnArithmeticOracleTests : IAsyncLifetime
{
    private const string DatabaseName = "SilentScanColumnArithmeticOracleTest";

    private readonly SqlServerOptions _options = SqlServerOptions.LocalDocker;
    private readonly DatabaseProvisioner _provisioner;

    public ColumnArithmeticOracleTests()
    {
        _provisioner = new DatabaseProvisioner(_options);
    }

    public async Task InitializeAsync()
    {
        await _provisioner.CreateFreshAsync(DatabaseName);
        await new ScriptDeployer(_options).DeployAsync(
            """
            CREATE TABLE dbo.Orders (Code VARCHAR(20) NOT NULL, OrderNo INT NOT NULL);
            GO
            CREATE INDEX IX_Orders_OrderNo ON dbo.Orders(OrderNo);
            GO
            INSERT INTO dbo.Orders(Code, OrderNo)
            SELECT TOP (5000) 'C' + CAST(ROW_NUMBER() OVER (ORDER BY (SELECT NULL)) AS VARCHAR(10)),
                   ROW_NUMBER() OVER (ORDER BY (SELECT NULL))
            FROM sys.all_objects a CROSS JOIN sys.all_objects b;
            GO
            UPDATE STATISTICS dbo.Orders WITH FULLSCAN;
            GO
            CREATE PROCEDURE dbo.ProbeArithmetic @x INT AS
            BEGIN
                SELECT Code FROM dbo.Orders WHERE OrderNo + 1 = @x;
            END
            GO
            CREATE PROCEDURE dbo.ProbeBareColumn @x INT AS
            BEGIN
                SELECT Code FROM dbo.Orders WHERE OrderNo = @x;
            END
            GO
            """,
            DatabaseName);
    }

    public async Task DisposeAsync() =>
        await _provisioner.DropIfExistsAsync(DatabaseName);

    private async Task<bool> HasIndexSeek(string probe)
    {
        var planXml = await new PlanXmlCapture(_options).CaptureAsync(DatabaseName, probe);
        return IndexAccessDetector.HasIndexSeek(planXml, "IX_Orders_OrderNo");
    }

    [Fact]
    public async Task ArithmeticOnColumn_NeverSeeks() =>
        Assert.False(await HasIndexSeek("EXEC dbo.ProbeArithmetic @x = 101;"));

    [Fact]
    public async Task BareColumnComparison_Seeks() =>
        Assert.True(await HasIndexSeek("EXEC dbo.ProbeBareColumn @x = 100;"));
}
