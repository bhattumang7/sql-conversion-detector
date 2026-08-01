using SilentScan.Verify;
using SilentScan.Verify.Deployment;
using SilentScan.Verify.Oracle;

namespace SilentScan.Tests.Integration;

/// <summary>
/// Phase 1.1 of docs/audit-remediation-plan.md: confirms against the real oracle that the
/// collation family a manifest's declaredCollation hint would supply actually produces the
/// plan shape VerdictClassifier predicts once that hint reaches a column with no COLLATE of its
/// own - SQL_* forces a scan, a Windows collation permits the dynamic range seek. Deploys the
/// collation on the column directly rather than via ALTER DATABASE ... COLLATE (which resets
/// the connection mid-session against this Docker image); the optimizer's behavior is identical
/// either way, since a column's *effective* collation is what matters, not its source. The
/// fallback wiring itself (manifest hint -&gt; DatabaseCatalog.DefaultCollation -&gt; an uncollated
/// column) is covered statically by ScanReportBuilderCollationTests.
/// </summary>
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

        Assert.True(ConvertImplicitDetector.FindColumnConversions(planXml).Count > 0);

        // docs/audit-remediation-plan.md Phase 5.1, audit finding C1: conversion presence alone
        // doesn't distinguish ScanForced from RangeSeek - both produce it. A genuine ScanForced
        // plan must ALSO lack the dynamic-seek machinery a RangeSeek verdict would show.
        Assert.DoesNotContain("GetRangeThroughConvert", planXml, StringComparison.Ordinal);
    }

    [Fact]
    public async Task WindowsFamilyManifestCollation_MatchesRangeSeekPrediction()
    {
        await DeployWithDatabaseCollationAsync("Latin1_General_CI_AS");

        var planXml = await new PlanXmlCapture(_options).CaptureAsync(
            DatabaseName, "DECLARE @p NVARCHAR(40) = N'Alice'; SELECT Id FROM dbo.Users WHERE DisplayName = @p;");

        Assert.True(ConvertImplicitDetector.FindColumnConversions(planXml).Count > 0);
        Assert.Contains("GetRangeThroughConvert", planXml, StringComparison.Ordinal);
    }
}
