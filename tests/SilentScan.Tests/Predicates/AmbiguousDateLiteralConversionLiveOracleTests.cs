using SilentScan.Core.Predicates;
using SilentScan.Tests.Support;

namespace SilentScan.Tests.Predicates;

[Trait("Category", "Oracle")]
public sealed class AmbiguousDateLiteralConversionLiveOracleTests
{
    [Fact]
    public async Task LiveDeployment_AmbiguousLiteralCastToDate_Fires()
    {
        var report = await EngineAuthoritativeScan.ScanAsync(
            """
            SELECT CAST('03/04/2026' AS date);
            """,
            minimumConfidence: FindingConfidence.Low);

        var finding = Assert.Single(report.Find<AmbiguousDateLiteralConversionFinding>("AmbiguousDateLiteralConversionScanner"));
        Assert.Equal("03/04/2026", finding.LiteralText);
    }

    [Fact]
    public async Task LiveDeployment_ConvertWithExplicitStyle_NeverFires()
    {
        var report = await EngineAuthoritativeScan.ScanAsync(
            """
            SELECT CONVERT(date, '03/04/2026', 103);
            """,
            minimumConfidence: FindingConfidence.Low);

        Assert.Empty(report.Find<AmbiguousDateLiteralConversionFinding>("AmbiguousDateLiteralConversionScanner"));
    }

    [Fact]
    public async Task LiveDeployment_IsoFormatLiteral_NeverFires()
    {
        var report = await EngineAuthoritativeScan.ScanAsync(
            """
            SELECT CAST('20260304' AS date);
            """,
            minimumConfidence: FindingConfidence.Low);

        Assert.Empty(report.Find<AmbiguousDateLiteralConversionFinding>("AmbiguousDateLiteralConversionScanner"));
    }
}
