using SilentScan.Core.Predicates;
using SilentScan.Tests.Support;

namespace SilentScan.Tests.Predicates;

[Trait("Category", "Oracle")]
public sealed class BackupOptionConflictLiveOracleTests
{
    [Fact]
    public async Task LiveDeployment_DifferentialWithCopyOnly_Fires()
    {
        var report = await EngineAuthoritativeScan.ScanAsync(
            """
            BACKUP DATABASE SomeDatabase TO DISK = 'nul' WITH DIFFERENTIAL, COPY_ONLY;
            """,
            minimumConfidence: FindingConfidence.Low);

        var finding = Assert.Single(report.Find<BackupOptionConflictFinding>("BackupOptionConflictScanner"));
        Assert.Equal(FindingConfidence.High, finding.Confidence);
    }

    [Fact]
    public async Task LiveDeployment_DifferentialWithoutCopyOnly_NeverFires()
    {
        var report = await EngineAuthoritativeScan.ScanAsync(
            """
            BACKUP DATABASE SomeDatabase TO DISK = 'nul' WITH DIFFERENTIAL;
            """,
            minimumConfidence: FindingConfidence.Low);

        Assert.Empty(report.Find<BackupOptionConflictFinding>("BackupOptionConflictScanner"));
    }

    [Fact]
    public async Task LiveDeployment_CopyOnlyWithoutDifferential_NeverFires()
    {
        var report = await EngineAuthoritativeScan.ScanAsync(
            """
            BACKUP DATABASE SomeDatabase TO DISK = 'nul' WITH COPY_ONLY;
            """,
            minimumConfidence: FindingConfidence.Low);

        Assert.Empty(report.Find<BackupOptionConflictFinding>("BackupOptionConflictScanner"));
    }

    [Fact]
    public async Task LiveDeployment_PlainBackup_NeverFires()
    {
        var report = await EngineAuthoritativeScan.ScanAsync(
            """
            BACKUP DATABASE SomeDatabase TO DISK = 'nul';
            """,
            minimumConfidence: FindingConfidence.Low);

        Assert.Empty(report.Find<BackupOptionConflictFinding>("BackupOptionConflictScanner"));
    }
}
