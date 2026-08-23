using SilentScan.Verify;
using SilentScan.Verify.Deployment;
using SilentScan.Verify.Oracle;

namespace SilentScan.Tests.Integration;

[Trait("Category", "Oracle")]
public sealed class SameFamilyWideningOracleTests : IAsyncLifetime
{
    private const string DatabaseName = "SilentScanWideningOracleTest";

    private readonly SqlServerOptions _options = SqlServerOptions.LocalDocker;
    private readonly DatabaseProvisioner _provisioner;

    public SameFamilyWideningOracleTests()
    {
        _provisioner = new DatabaseProvisioner(_options);
    }

    public async Task InitializeAsync()
    {
        await _provisioner.CreateFreshAsync(DatabaseName);
        await new ScriptDeployer(_options).DeployAsync(
            """
            CREATE TABLE dbo.People (PersonId INT NOT NULL PRIMARY KEY, IsPermittedToLogon BIT NOT NULL);
            GO
            CREATE INDEX IX_People_IsPermittedToLogon ON dbo.People(IsPermittedToLogon);
            GO
            CREATE TABLE dbo.PurchaseOrders (Id INT NOT NULL PRIMARY KEY, ExpectedDeliveryDate DATE NULL);
            GO
            CREATE INDEX IX_PurchaseOrders_ExpectedDeliveryDate ON dbo.PurchaseOrders(ExpectedDeliveryDate);
            GO
            """,
            DatabaseName);
    }

    public async Task DisposeAsync() =>
        await _provisioner.DropIfExistsAsync(DatabaseName);

    private async Task<bool> HasColumnConversion(string probe)
    {
        var planXml = await new PlanXmlCapture(_options).CaptureAsync(DatabaseName, probe);
        return ConvertImplicitDetector.FindColumnConversions(planXml).Count > 0;
    }

    [Fact]
    public async Task BitColumn_VsIntegerLiteral_NoConvertImplicitInPlan() =>
        Assert.False(await HasColumnConversion("SELECT PersonId FROM dbo.People WHERE IsPermittedToLogon = 0;"));

    [Fact]
    public async Task BitColumn_VsBigIntParameter_NoConvertImplicitInPlan() =>
        Assert.False(await HasColumnConversion("DECLARE @p BIGINT = 0; SELECT PersonId FROM dbo.People WHERE IsPermittedToLogon = @p;"));

    [Fact]
    public async Task BitColumn_VsVarcharParameter_ConvertImplicitAppliesToTheParameterNotTheColumn() =>
        Assert.False(await HasColumnConversion("DECLARE @p VARCHAR(5) = '0'; SELECT PersonId FROM dbo.People WHERE IsPermittedToLogon = @p;"));

    [Fact]
    public async Task DateColumn_VsDateTimeParameter_NoConvertImplicitInPlan() =>
        Assert.False(await HasColumnConversion(
            "DECLARE @p DATETIME = '2024-01-01'; SELECT Id FROM dbo.PurchaseOrders WHERE ExpectedDeliveryDate >= @p;"));

    [Fact]
    public async Task DateColumn_VsVarcharParameter_ConvertImplicitAppliesToTheParameterNotTheColumn() =>
        Assert.False(await HasColumnConversion(
            "DECLARE @p VARCHAR(20) = '2024-01-01'; SELECT Id FROM dbo.PurchaseOrders WHERE ExpectedDeliveryDate >= @p;"));
}
