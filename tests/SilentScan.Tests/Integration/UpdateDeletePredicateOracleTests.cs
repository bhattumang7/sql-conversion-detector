using SilentScan.Verify;
using SilentScan.Verify.Deployment;
using SilentScan.Verify.Oracle;

namespace SilentScan.Tests.Integration;

[Trait("Category", "Oracle")]
public sealed class UpdateDeletePredicateOracleTests : IAsyncLifetime
{
    private const string DatabaseName = "SilentScanUpdateDeleteOracleTest";

    private readonly SqlServerOptions _options = SqlServerOptions.LocalDocker;
    private readonly DatabaseProvisioner _provisioner;

    public UpdateDeletePredicateOracleTests()
    {
        _provisioner = new DatabaseProvisioner(_options);
    }

    public async Task InitializeAsync()
    {
        await _provisioner.CreateFreshAsync(DatabaseName);
        await new ScriptDeployer(_options).DeployAsync(
            """
            CREATE TABLE dbo.Sessions (Token VARCHAR(64) COLLATE SQL_Latin1_General_CP1_CI_AS NOT NULL, IsExpired BIT NOT NULL);
            GO
            CREATE INDEX IX_Sessions_Token ON dbo.Sessions(Token);
            GO
            """,
            DatabaseName);
    }

    public async Task DisposeAsync() =>
        await _provisioner.DropIfExistsAsync(DatabaseName);

    private async Task<bool> HasColumnConversion(string probe, string columnName)
    {
        var planXml = await new PlanXmlCapture(_options).CaptureAsync(DatabaseName, probe);
        return ConvertImplicitDetector.FindColumnConversions(planXml).Any(c => c.Column == columnName);
    }

    [Fact]
    public async Task UpdateWhereClause_VarcharColumnVsNVarcharParam_ConvertImplicitOnColumn() =>
        Assert.True(await HasColumnConversion(
            "DECLARE @p NVARCHAR(64) = N'x'; UPDATE dbo.Sessions SET IsExpired = 1 WHERE Token = @p;", "Token"));

    [Fact]
    public async Task DeleteWhereClause_VarcharColumnVsNVarcharParam_ConvertImplicitOnColumn() =>
        Assert.True(await HasColumnConversion(
            "DECLARE @p NVARCHAR(64) = N'x'; DELETE FROM dbo.Sessions WHERE Token = @p;", "Token"));

    [Fact]
    public async Task UpdateWhereClause_VarcharColumnVsVarcharParam_NoColumnConversion() =>
        Assert.False(await HasColumnConversion(
            "DECLARE @p VARCHAR(64) = 'x'; UPDATE dbo.Sessions SET IsExpired = 1 WHERE Token = @p;", "Token"));
}
