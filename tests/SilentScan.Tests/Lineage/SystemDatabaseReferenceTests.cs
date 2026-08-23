using SilentScan.Core.Reporting;
using SilentScan.Tests.Support;

namespace SilentScan.Tests.Lineage;

[Trait("Category", "Oracle")]
public sealed class SystemDatabaseReferenceTests
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

    [Theory]
    [InlineData("msdb")]
    [InlineData("master")]
    [InlineData("tempdb")]
    [InlineData("model")]
    [InlineData("MSDB")]
    public async Task ReferenceToSystemDatabase_GetsSpecificOutOfScopeReason(string systemDatabase)
    {
        var report = await Scan($"SELECT name FROM {systemDatabase}.sys.objects WHERE name = 'x';");

        Assert.Contains(report.SkippedConstructs, s =>
            s.Reason.Contains("system database", StringComparison.Ordinal)
            && s.Reason.Contains("intentionally out of scope", StringComparison.Ordinal));
        Assert.DoesNotContain(report.SkippedConstructs, s => s.Reason.Contains("has no known DDL", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ReferenceToGenuineExternalDatabase_StaysTheGenericNoKnownDdlReason()
    {
        var report = await Scan("""
            CREATE TABLE dbo.Shipments (TrackingNo varchar(30) NOT NULL);
            GO
            SELECT 1 FROM ArchiveDb.dbo.Shipments WHERE TrackingNo = N'T1';
            """);

        Assert.Contains(report.SkippedConstructs, s =>
            s.Reason.Contains("ArchiveDb.dbo.Shipments", StringComparison.Ordinal)
            && s.Reason.Contains("has no known DDL", StringComparison.Ordinal));
        Assert.DoesNotContain(report.SkippedConstructs, s => s.Reason.Contains("system database", StringComparison.Ordinal));
    }
}
