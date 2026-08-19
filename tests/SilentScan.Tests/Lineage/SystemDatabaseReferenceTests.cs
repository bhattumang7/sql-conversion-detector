using SilentScan.Core.Reporting;
using SilentScan.Tests.Support;

namespace SilentScan.Tests.Lineage;

/// <summary>
/// Regression coverage for the cross-database-reference scope decision: corpus measurement (466
/// three-part references across the pinned corpus - msdb 282, master 123, tempdb 31, model 7,
/// zero genuine user databases) proved multi-database catalog support would gain zero real
/// findings, since every occurrence was a DBA/admin script querying one of SQL Server's four
/// built-in system databases, for which no DDL will ever exist to catalog. Rather than build
/// multi-catalog support for a workload with no findings behind it, a reference to one of the
/// four gets a distinct, specific skip-ledger reason instead of the generic "no known DDL" one
/// (<see cref="Lineage.FromScopeResolver"/>'s IsSystemDatabaseReference) - so a reader can tell
/// "we chose not to model this" from "this is a real gap". A reference to a genuine external
/// user database (unavailable to us, but not a system database) still gets the generic reason,
/// since that IS a real, nameable gap - see
/// Diagnostics/KnownGapCharacterizationTests.CrossDatabaseReference_GetsAKeyNothingPopulates_NoTypedFinding.
/// </summary>
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
        // ArchiveDb is a real, nameable external database - not one of the four built-in
        // system databases - so this is a genuine gap, not an intentional scope boundary, and
        // must keep the generic reason rather than being misclassified as "out of scope".
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
