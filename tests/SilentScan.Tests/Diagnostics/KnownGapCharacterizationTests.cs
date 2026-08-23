using SilentScan.Core.Predicates;
using SilentScan.Core.Reporting;
using SilentScan.Core.Rules;
using SilentScan.Tests.Support;
using SilentScan.Core.TypeInference;

namespace SilentScan.Tests.Diagnostics;

[Trait("Category", "Oracle")]
public sealed class KnownGapCharacterizationTests
{
    private static async Task<ScanReport> Scan(string sql)
    {
        var report = await EngineAuthoritativeScan.ScanAsync(sql, "SQL_Latin1_General_CP1_CI_AS");

        foreach (var file in report.ParseHealth.Files)
        {
            Assert.Empty(file.Errors);
        }

        return report;
    }

    [Fact]
    public async Task CrossDatabaseReference_GetsAKeyNothingPopulates_NoTypedFinding()
    {

        var report = await Scan("""
            CREATE TABLE dbo.Shipments (TrackingNo varchar(30) NOT NULL, INDEX IX_TrackingNo (TrackingNo));
            GO
            SELECT 1 FROM ArchiveDb.dbo.Shipments WHERE TrackingNo = N'T1';
            """);

        Assert.Empty(report.TypedFindings);
        Assert.Contains(report.SkippedConstructs, s => s.Reason.Contains("ArchiveDb.dbo.Shipments", StringComparison.Ordinal));
    }

}
