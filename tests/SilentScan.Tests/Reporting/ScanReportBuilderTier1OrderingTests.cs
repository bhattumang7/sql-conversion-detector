using SilentScan.Tests.Support;
using SilentScan.Core.Predicates;

namespace SilentScan.Tests.Reporting;

[Trait("Category", "Oracle")]
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

        Assert.True(report.Find<SargabilityFinding>("NonSargablePredicateScanner").Count >= 2);
        var firstIndexedPosition = report.Find<SargabilityFinding>("NonSargablePredicateScanner").ToList().FindIndex(f => f.Indexed == true);
        var firstUnindexedPosition = report.Find<SargabilityFinding>("NonSargablePredicateScanner").ToList().FindIndex(f => f.Indexed == false);

        Assert.True(firstIndexedPosition >= 0 && firstUnindexedPosition >= 0);
        Assert.True(firstIndexedPosition < firstUnindexedPosition,
            "an indexed-column finding must rank before an unindexed-column finding");
    }
}
