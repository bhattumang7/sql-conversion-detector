using SilentScan.Verify;
using SilentScan.Verify.Deployment;
using SilentScan.Verify.Oracle;

namespace SilentScan.Tests.Integration;

/// <summary>
/// docs/audit-remediation-plan.md Phase 4.1 "Done when": real-world-sourced UPDATE and DELETE
/// fixtures produce findings confirmed by oracle probe. Pass 3 previously pushed no FROM scope
/// at all for UPDATE/DELETE, so their WHERE-clause predicates were invisible - this confirms not
/// just that the static classifier now sees them (TypedPredicateExtractorTests), but that the
/// real SQL Server plan for the exact same predicate shape actually shows CONVERT_IMPLICIT on
/// the column, matching what the classifier says.
/// </summary>
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

    private async Task<bool> HasColumnConversion(string probe)
    {
        var planXml = await new PlanXmlCapture(_options).CaptureAsync(DatabaseName, probe);
        return ConvertImplicitDetector.FindColumnConversions(planXml).Count > 0;
    }

    [Fact]
    public async Task UpdateWhereClause_VarcharColumnVsNVarcharParam_ConvertImplicitOnColumn() =>
        // Same shape TypedPredicateExtractorTests.Extract_UpdateWhereClause_NoFromExtension_
        // ResolvesAgainstTarget classifies as ScanForced - confirms the real optimizer agrees.
        Assert.True(await HasColumnConversion(
            "DECLARE @p NVARCHAR(64) = N'x'; UPDATE dbo.Sessions SET IsExpired = 1 WHERE Token = @p;"));

    [Fact]
    public async Task DeleteWhereClause_VarcharColumnVsNVarcharParam_ConvertImplicitOnColumn() =>
        Assert.True(await HasColumnConversion(
            "DECLARE @p NVARCHAR(64) = N'x'; DELETE FROM dbo.Sessions WHERE Token = @p;"));
}
