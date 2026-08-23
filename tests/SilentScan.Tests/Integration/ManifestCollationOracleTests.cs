using SilentScan.Verify;
using SilentScan.Verify.Deployment;
using SilentScan.Verify.Oracle;

namespace SilentScan.Tests.Integration;

[Trait("Category", "Oracle")]
public sealed class ManifestCollationOracleTests : IAsyncLifetime
{
    private const string DatabaseName = "SilentScanManifestCollationOracleTest";

    private readonly SqlServerOptions _options = SqlServerOptions.LocalDocker;
    private readonly DatabaseProvisioner _provisioner;

    public ManifestCollationOracleTests()
    {
        _provisioner = new DatabaseProvisioner(_options);
    }

    public Task DisposeAsync() => _provisioner.DropIfExistsAsync(DatabaseName);

    public Task InitializeAsync() => Task.CompletedTask;

    private async Task DeployWithDatabaseCollationAsync(string collation)
    {
        await _provisioner.CreateFreshAsync(DatabaseName);
        await new ScriptDeployer(_options).DeployAsync(
            $"""
            CREATE TABLE dbo.Users (Id INT NOT NULL PRIMARY KEY, DisplayName VARCHAR(40) COLLATE {collation} NOT NULL);
            GO
            CREATE INDEX IX_Users_DisplayName ON dbo.Users(DisplayName);
            GO
            """,
            DatabaseName);
    }

    private async Task<bool> HasColumnConversion(string probe)
    {
        var planXml = await new PlanXmlCapture(_options).CaptureAsync(DatabaseName, probe);
        return ConvertImplicitDetector.FindColumnConversions(planXml).Count > 0;
    }

    [Fact]
    public async Task SqlFamilyManifestCollation_MatchesScanForcedPrediction()
    {
        await DeployWithDatabaseCollationAsync("SQL_Latin1_General_CP1_CI_AS");

        var planXml = await new PlanXmlCapture(_options).CaptureAsync(
            DatabaseName, "DECLARE @p NVARCHAR(40) = N'Alice'; SELECT Id FROM dbo.Users WHERE DisplayName = @p;");

        Assert.Contains(ConvertImplicitDetector.FindColumnConversions(planXml), c => c.Column == "DisplayName");

        Assert.DoesNotContain("GetRangeThroughConvert", planXml, StringComparison.Ordinal);
    }

    [Fact]
    public async Task WindowsFamilyManifestCollation_MatchesRangeSeekPrediction()
    {
        await DeployWithDatabaseCollationAsync("Latin1_General_CI_AS");

        var planXml = await new PlanXmlCapture(_options).CaptureAsync(
            DatabaseName, "DECLARE @p NVARCHAR(40) = N'Alice'; SELECT Id FROM dbo.Users WHERE DisplayName = @p;");

        Assert.Contains(ConvertImplicitDetector.FindColumnConversions(planXml), c => c.Column == "DisplayName");
        Assert.Contains("GetRangeThroughConvert", planXml, StringComparison.Ordinal);
    }
}
