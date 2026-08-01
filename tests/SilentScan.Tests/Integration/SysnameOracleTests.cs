using SilentScan.Verify;
using SilentScan.Verify.Deployment;
using SilentScan.Verify.Oracle;

namespace SilentScan.Tests.Integration;

/// <summary>
/// docs/audit-remediation-plan.md Phase 6.2: sysname must behave exactly like nvarchar(128) for
/// implicit-conversion purposes, not just be typed that way statically. Verified directly
/// against the real engine before relying on it: a VARCHAR column vs an NVARCHAR/sysname value
/// forces the varchar column to convert either way, and (checked separately, not asserted here
/// to keep this test focused) a VARCHAR value against a sysname column produces no conversion
/// at all - the same both-directions behavior an ordinary nvarchar(128) column would show.
/// </summary>
public sealed class SysnameOracleTests : IAsyncLifetime
{
    private const string DatabaseName = "SilentScanSysnameOracleTest";

    private readonly SqlServerOptions _options = SqlServerOptions.LocalDocker;
    private readonly DatabaseProvisioner _provisioner;

    public SysnameOracleTests()
    {
        _provisioner = new DatabaseProvisioner(_options);
    }

    public async Task InitializeAsync()
    {
        await _provisioner.CreateFreshAsync(DatabaseName);
        await new ScriptDeployer(_options).DeployAsync(
            """
            CREATE TABLE dbo.T (Id INT NOT NULL PRIMARY KEY, Code VARCHAR(20) COLLATE SQL_Latin1_General_CP1_CI_AS NOT NULL);
            GO
            CREATE INDEX IX_T_Code ON dbo.T(Code);
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
    public async Task VarcharColumnVsSysnameVariable_ColumnConverts() =>
        // Same shape as the classic varchar-column-vs-nvarchar-parameter bug this tool targets -
        // sysname outranks varchar in precedence exactly like nvarchar does.
        Assert.True(await HasColumnConversion("DECLARE @p sysname = N'x'; SELECT Id FROM dbo.T WHERE Code = @p;"));
}
