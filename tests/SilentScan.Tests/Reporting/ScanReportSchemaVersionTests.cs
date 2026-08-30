using SilentScan.Core.Reporting;

namespace SilentScan.Tests.Reporting;

public sealed class ScanReportSchemaVersionTests
{
    private const int ExpectedSchemaVersion = 77;

    [Fact]
    public void CurrentSchemaVersion_MatchesRecordedValue()
    {
        Assert.Equal(ExpectedSchemaVersion, ScanReport.CurrentSchemaVersion);
    }

    [Fact]
    public void Build_DefaultsSchemaVersionToCurrentSchemaVersion()
    {
        var report = Support.TestScanReports.Build();

        Assert.Equal(ScanReport.CurrentSchemaVersion, report.SchemaVersion);
    }
}
