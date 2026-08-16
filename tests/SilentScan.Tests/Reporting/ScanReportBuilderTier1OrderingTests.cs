using SilentScan.Tests.Support;

namespace SilentScan.Tests.Reporting;

/// <summary>
/// docs/detection-checklist.md Tier 1 "Type-aware upgrade of the sargability stream" #5, index-
/// existence weighting: a Tier-1 (syntactic non-sargability) finding on an indexed column is a
/// real lost seek, on an unindexed column it's noise (there was no seek to lose). Mirrors
/// TypedFindings' own existing <c>ThenByDescending(f => f.Column.Indexed)</c> ranking, which
/// Tier1Findings never had until now - this test is the regression guard for that gap.
/// </summary>
public sealed class ScanReportBuilderTier1OrderingTests
{
    private const string Sql = """
        CREATE TABLE dbo.Unindexed (Code VARCHAR(20) NOT NULL);
        GO
        CREATE TABLE dbo.Indexed (Code VARCHAR(20) NOT NULL, INDEX IX_Indexed_Code (Code));
        GO
        SELECT 1 FROM dbo.Unindexed WHERE UPPER(Code) = 'X';
        GO
        SELECT 1 FROM dbo.Indexed WHERE UPPER(Code) = 'X';
        """;

    [Fact]
    public async Task IndexedColumnFinding_RanksBeforeUnindexedColumnFinding()
    {
        var report = await EngineAuthoritativeScan.ScanAsync(Sql);

        Assert.True(report.Tier1Findings.Count >= 2);
        var firstIndexedPosition = report.Tier1Findings.ToList().FindIndex(f => f.Indexed == true);
        var firstUnindexedPosition = report.Tier1Findings.ToList().FindIndex(f => f.Indexed == false);

        Assert.True(firstIndexedPosition >= 0 && firstUnindexedPosition >= 0);
        Assert.True(firstIndexedPosition < firstUnindexedPosition,
            "an indexed-column finding must rank before an unindexed-column finding");
    }
}
