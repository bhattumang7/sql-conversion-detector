using SilentScan.Verify;
using SilentScan.Verify.Deployment;
using SilentScan.Verify.Oracle;

namespace SilentScan.Tests.Integration;

[Trait("Category", "Oracle")]
public sealed class DateFunctionOnColumnOracleTests : IAsyncLifetime
{
    private const string DatabaseName = "SilentScanDateFunctionOracleTest";

    private readonly SqlServerOptions _options = SqlServerOptions.LocalDocker;
    private readonly DatabaseProvisioner _provisioner;

    public DateFunctionOnColumnOracleTests()
    {
        _provisioner = new DatabaseProvisioner(_options);
    }

    public async Task InitializeAsync()
    {
        await _provisioner.CreateFreshAsync(DatabaseName);
        await new ScriptDeployer(_options).DeployAsync(
            """
            CREATE TABLE dbo.Orders (Code VARCHAR(20) NOT NULL, OrderDate DATETIME2(3) NOT NULL);
            GO
            CREATE INDEX IX_Orders_OrderDate ON dbo.Orders(OrderDate);
            GO
            INSERT INTO dbo.Orders(Code, OrderDate)
            SELECT TOP (5000) 'C' + CAST(ROW_NUMBER() OVER (ORDER BY (SELECT NULL)) AS VARCHAR(10)),
                   DATEADD(DAY, ROW_NUMBER() OVER (ORDER BY (SELECT NULL)), '2020-01-01')
            FROM sys.all_objects a CROSS JOIN sys.all_objects b;
            GO
            UPDATE STATISTICS dbo.Orders WITH FULLSCAN;
            GO
            CREATE PROCEDURE dbo.ProbeYear @x INT AS
            BEGIN
                SELECT Code FROM dbo.Orders WHERE YEAR(OrderDate) = @x;
            END
            GO
            CREATE PROCEDURE dbo.ProbeBareColumn @x DATETIME2(3) AS
            BEGIN
                SELECT Code FROM dbo.Orders WHERE OrderDate = @x;
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
        return IndexAccessDetector.HasIndexSeek(planXml, "IX_Orders_OrderDate");
    }

    [Fact]
    public async Task YearOnColumn_NeverSeeks() =>
        Assert.False(await HasIndexSeek("EXEC dbo.ProbeYear @x = 2021;"));

    [Fact]
    public async Task BareColumnComparison_Seeks() =>
        Assert.True(await HasIndexSeek("EXEC dbo.ProbeBareColumn @x = '2021-01-01';"));
}
