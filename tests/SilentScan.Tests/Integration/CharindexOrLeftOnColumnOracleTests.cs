using SilentScan.Verify;
using SilentScan.Verify.Deployment;
using SilentScan.Verify.Oracle;

namespace SilentScan.Tests.Integration;

[Trait("Category", "Oracle")]
public sealed class CharindexOrLeftOnColumnOracleTests : IAsyncLifetime
{
    private const string DatabaseName = "SilentScanCharindexOrLeftOracleTest";

    private readonly SqlServerOptions _options = SqlServerOptions.LocalDocker;
    private readonly DatabaseProvisioner _provisioner;

    public CharindexOrLeftOnColumnOracleTests()
    {
        _provisioner = new DatabaseProvisioner(_options);
    }

    public async Task InitializeAsync()
    {
        await _provisioner.CreateFreshAsync(DatabaseName);
        await new ScriptDeployer(_options).DeployAsync(
            """
            CREATE TABLE dbo.Orders (Code VARCHAR(20) NOT NULL);
            GO
            CREATE INDEX IX_Orders_Code ON dbo.Orders(Code);
            GO
            INSERT INTO dbo.Orders(Code)
            SELECT TOP (5000) 'C' + CAST(ROW_NUMBER() OVER (ORDER BY (SELECT NULL)) AS VARCHAR(10))
            FROM sys.all_objects a CROSS JOIN sys.all_objects b;
            GO
            UPDATE STATISTICS dbo.Orders WITH FULLSCAN;
            GO
            CREATE PROCEDURE dbo.ProbeLeft @x VARCHAR(20) AS
            BEGIN
                SELECT Code FROM dbo.Orders WHERE LEFT(Code, 3) = @x;
            END
            GO
            CREATE PROCEDURE dbo.ProbeCharindex @x VARCHAR(20) AS
            BEGIN
                SELECT Code FROM dbo.Orders WHERE CHARINDEX(@x, Code) = 1;
            END
            GO
            CREATE PROCEDURE dbo.ProbeBareColumn @x VARCHAR(20) AS
            BEGIN
                SELECT Code FROM dbo.Orders WHERE Code = @x;
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
        return IndexAccessDetector.HasIndexSeek(planXml, "IX_Orders_Code");
    }

    [Fact]
    public async Task LeftPrefixMatch_NeverSeeks() =>
        Assert.False(await HasIndexSeek("EXEC dbo.ProbeLeft @x = 'C10';"));

    [Fact]
    public async Task CharindexPrefixMatch_NeverSeeks() =>
        Assert.False(await HasIndexSeek("EXEC dbo.ProbeCharindex @x = 'C10';"));

    [Fact]
    public async Task BareColumnComparison_Seeks() =>
        Assert.True(await HasIndexSeek("EXEC dbo.ProbeBareColumn @x = 'C10';"));
}
