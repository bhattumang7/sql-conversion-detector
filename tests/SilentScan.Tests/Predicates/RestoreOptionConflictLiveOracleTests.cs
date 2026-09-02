using SilentScan.Core.Predicates;
using SilentScan.Tests.Support;

namespace SilentScan.Tests.Predicates;

[Trait("Category", "Oracle")]
public sealed class RestoreOptionConflictLiveOracleTests
{
    [Fact]
    public async Task LiveDeployment_RecoveryWithNoRecovery_Fires()
    {
        var report = await EngineAuthoritativeScan.ScanAsync(
            """
            RESTORE DATABASE SomeDatabase FROM DISK = 'nul' WITH RECOVERY, NORECOVERY;
            """,
            minimumConfidence: FindingConfidence.Low);

        var finding = Assert.Single(report.Find<RestoreOptionConflictFinding>("RestoreOptionConflictScanner"));
        Assert.Equal(RestoreOptionConflictKind.RecoveryAndNoRecovery, finding.Kind);
        Assert.Equal(FindingConfidence.High, finding.Confidence);
    }

    [Fact]
    public async Task LiveDeployment_RecoveryWithStandby_Fires()
    {
        var report = await EngineAuthoritativeScan.ScanAsync(
            """
            RESTORE DATABASE SomeDatabase FROM DISK = 'nul' WITH RECOVERY, STANDBY = 'nul';
            """,
            minimumConfidence: FindingConfidence.Low);

        var finding = Assert.Single(report.Find<RestoreOptionConflictFinding>("RestoreOptionConflictScanner"));
        Assert.Equal(RestoreOptionConflictKind.RecoveryAndStandby, finding.Kind);
    }

    [Fact]
    public async Task LiveDeployment_NoRecoveryWithStandby_Fires()
    {
        var report = await EngineAuthoritativeScan.ScanAsync(
            """
            RESTORE DATABASE SomeDatabase FROM DISK = 'nul' WITH NORECOVERY, STANDBY = 'nul';
            """,
            minimumConfidence: FindingConfidence.Low);

        var finding = Assert.Single(report.Find<RestoreOptionConflictFinding>("RestoreOptionConflictScanner"));
        Assert.Equal(RestoreOptionConflictKind.NoRecoveryAndStandby, finding.Kind);
    }

    [Fact]
    public async Task LiveDeployment_RecoveryAlone_NeverFires()
    {
        var report = await EngineAuthoritativeScan.ScanAsync(
            """
            RESTORE DATABASE SomeDatabase FROM DISK = 'nul' WITH RECOVERY;
            """,
            minimumConfidence: FindingConfidence.Low);

        Assert.Empty(report.Find<RestoreOptionConflictFinding>("RestoreOptionConflictScanner"));
    }

    [Fact]
    public async Task LiveDeployment_NoRecoveryAlone_NeverFires()
    {
        var report = await EngineAuthoritativeScan.ScanAsync(
            """
            RESTORE DATABASE SomeDatabase FROM DISK = 'nul' WITH NORECOVERY;
            """,
            minimumConfidence: FindingConfidence.Low);

        Assert.Empty(report.Find<RestoreOptionConflictFinding>("RestoreOptionConflictScanner"));
    }
}
