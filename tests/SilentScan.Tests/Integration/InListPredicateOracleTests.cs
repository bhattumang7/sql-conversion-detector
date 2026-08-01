using SilentScan.Verify;
using SilentScan.Verify.Deployment;
using SilentScan.Verify.Oracle;

namespace SilentScan.Tests.Integration;

/// <summary>
/// docs/audit-remediation-plan.md Phase 4.3 "Done when": IN-list findings are oracle-confirmed,
/// not just statically classified. Confirms the specific claim
/// TypedPredicateExtractorTests.Extract_InListOneNvarcharLiteralAmongVarchar_SqlCollation_
/// ScanForced depends on - that a SINGLE higher-precedence literal anywhere in an otherwise-
/// homogeneous IN list is enough to force the real optimizer to convert the column for the
/// whole comparison, not just element-by-element.
/// </summary>
[Trait("Category", "Oracle")]
public sealed class InListPredicateOracleTests : IAsyncLifetime
{
    private const string DatabaseName = "SilentScanInListOracleTest";

    private readonly SqlServerOptions _options = SqlServerOptions.LocalDocker;
    private readonly DatabaseProvisioner _provisioner;

    public InListPredicateOracleTests()
    {
        _provisioner = new DatabaseProvisioner(_options);
    }

    public async Task InitializeAsync()
    {
        await _provisioner.CreateFreshAsync(DatabaseName);
        await new ScriptDeployer(_options).DeployAsync(
            """
            CREATE TABLE dbo.T (Col VARCHAR(20) COLLATE SQL_Latin1_General_CP1_CI_AS NOT NULL);
            GO
            CREATE INDEX IX_T_Col ON dbo.T(Col);
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
    public async Task HomogeneousVarcharList_NoColumnConversion() =>
        Assert.False(await HasColumnConversion("SELECT Col FROM dbo.T WHERE Col IN ('a', 'b', 'c');"));

    [Fact]
    public async Task SingleNvarcharLiteralAmongVarchar_ColumnConverts() =>
        Assert.True(await HasColumnConversion("SELECT Col FROM dbo.T WHERE Col IN ('a', N'b', 'c');"));

    [Fact]
    public async Task HomogeneousNvarcharList_ColumnConverts() =>
        Assert.True(await HasColumnConversion("SELECT Col FROM dbo.T WHERE Col IN (N'a', N'b', N'c');"));
}
