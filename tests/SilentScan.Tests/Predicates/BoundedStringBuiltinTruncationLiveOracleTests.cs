using SilentScan.Core.Predicates;
using SilentScan.Tests.Support;

namespace SilentScan.Tests.Predicates;

[Trait("Category", "Oracle")]
public sealed class BoundedStringBuiltinTruncationLiveOracleTests
{
    [Fact]
    public async Task LiveDeployment_ReplicateNonMaxLiteralPastCap_Fires()
    {
        var report = await EngineAuthoritativeScan.ScanAsync(
            """
            CREATE PROCEDURE dbo.usp_ReplicateOverCap AS
            BEGIN
                SELECT REPLICATE('abcdefghij', 900);
            END
            """,
            minimumConfidence: FindingConfidence.Low);

        var finding = Assert.Single(report.Find<BoundedStringBuiltinTruncationFinding>("BoundedStringBuiltinTruncationScanner"));
        Assert.Equal(BoundedStringBuiltinTruncationFindingKind.ReplicateResultTruncated, finding.Kind);
        Assert.Equal("REPLICATE", finding.FunctionName);
        Assert.Equal(9000, finding.ComputedLength);
        Assert.Equal(8000, finding.CapBytes);
        Assert.Equal(FindingConfidence.High, finding.Confidence);
    }

    [Fact]
    public async Task LiveDeployment_ReplicateNationalLiteralPastCap_UsesUnicodeCap()
    {
        var report = await EngineAuthoritativeScan.ScanAsync(
            """
            CREATE PROCEDURE dbo.usp_ReplicateUnicodeOverCap AS
            BEGIN
                SELECT REPLICATE(N'ab', 3000);
            END
            """,
            minimumConfidence: FindingConfidence.Low);

        var finding = Assert.Single(report.Find<BoundedStringBuiltinTruncationFinding>("BoundedStringBuiltinTruncationScanner"));
        Assert.Equal(BoundedStringBuiltinTruncationFindingKind.ReplicateResultTruncated, finding.Kind);
        Assert.Equal(6000, finding.ComputedLength);
        Assert.Equal(4000, finding.CapBytes);
    }

    [Fact]
    public async Task LiveDeployment_ReplicateWithinCap_NeverFires()
    {
        var report = await EngineAuthoritativeScan.ScanAsync(
            """
            CREATE PROCEDURE dbo.usp_ReplicateWithinCap AS
            BEGIN
                SELECT REPLICATE('abcdefghij', 100);
            END
            """,
            minimumConfidence: FindingConfidence.Low);

        Assert.Empty(report.Find<BoundedStringBuiltinTruncationFinding>("BoundedStringBuiltinTruncationScanner"));
    }

    [Fact]
    public async Task LiveDeployment_ReplicateMaxTypedSource_NeverFires()
    {
        var report = await EngineAuthoritativeScan.ScanAsync(
            """
            CREATE PROCEDURE dbo.usp_ReplicateMaxSource AS
            BEGIN
                SELECT REPLICATE(CAST('abcdefghij' AS VARCHAR(MAX)), 900);
            END
            """,
            minimumConfidence: FindingConfidence.Low);

        Assert.Empty(report.Find<BoundedStringBuiltinTruncationFinding>("BoundedStringBuiltinTruncationScanner"));
    }

    [Fact]
    public async Task LiveDeployment_ReplicateNonFoldableCount_DeclinesRatherThanGuessing()
    {
        var report = await EngineAuthoritativeScan.ScanAsync(
            """
            CREATE PROCEDURE dbo.usp_ReplicateVariableCount AS
            BEGIN
                DECLARE @Count INT = 900;
                SELECT REPLICATE('abcdefghij', @Count);
            END
            """,
            minimumConfidence: FindingConfidence.Low);

        Assert.Empty(report.Find<BoundedStringBuiltinTruncationFinding>("BoundedStringBuiltinTruncationScanner"));
    }

    [Fact]
    public async Task LiveDeployment_ReplaceGrowingReplacementPastCap_Fires()
    {
        var report = await EngineAuthoritativeScan.ScanAsync(
            """
            CREATE PROCEDURE dbo.usp_ReplaceOverCap AS
            BEGIN
                SELECT REPLACE('xxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxx', 'x', 'yyyyyyyyyyyyyyyyyyyyyyyyyyyyyyyyyyyyyyyyyyyyyyyyyyyyyyyyyyyyyyyyyyyyyyyyyyyyyyyyyyyyyyyyyy');
            END
            """,
            minimumConfidence: FindingConfidence.Low);

        var finding = Assert.Single(report.Find<BoundedStringBuiltinTruncationFinding>("BoundedStringBuiltinTruncationScanner"));
        Assert.Equal(BoundedStringBuiltinTruncationFindingKind.ReplaceResultTruncated, finding.Kind);
        Assert.Equal("REPLACE", finding.FunctionName);
        Assert.Equal(9000, finding.ComputedLength);
        Assert.Equal(8000, finding.CapBytes);
    }

    [Fact]
    public async Task LiveDeployment_ReplaceMaxTypedInput_NeverFires()
    {
        var report = await EngineAuthoritativeScan.ScanAsync(
            """
            CREATE PROCEDURE dbo.usp_ReplaceMaxInput AS
            BEGIN
                SELECT REPLACE(CAST('xxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxx' AS VARCHAR(MAX)), 'x', 'yyyyyyyyyyyyyyyyyyyyyyyyyyyyyyyyyyyyyyyyyyyyyyyyyyyyyyyyyyyyyyyyyyyyyyyyyyyyyyyyyyyyyyyyyy');
            END
            """,
            minimumConfidence: FindingConfidence.Low);

        Assert.Empty(report.Find<BoundedStringBuiltinTruncationFinding>("BoundedStringBuiltinTruncationScanner"));
    }

    [Fact]
    public async Task LiveDeployment_ReplaceShrinkingReplacement_NeverFires()
    {
        var report = await EngineAuthoritativeScan.ScanAsync(
            """
            CREATE PROCEDURE dbo.usp_ReplaceShrinking AS
            BEGIN
                SELECT REPLACE('abcabcabc', 'abc', 'x');
            END
            """,
            minimumConfidence: FindingConfidence.Low);

        Assert.Empty(report.Find<BoundedStringBuiltinTruncationFinding>("BoundedStringBuiltinTruncationScanner"));
    }

    [Fact]
    public async Task LiveDeployment_ReplaceEmptyFromArgument_NeverFires()
    {
        var report = await EngineAuthoritativeScan.ScanAsync(
            """
            CREATE PROCEDURE dbo.usp_ReplaceEmptyFrom AS
            BEGIN
                SELECT REPLACE('abc', '', 'X');
            END
            """,
            minimumConfidence: FindingConfidence.Low);

        Assert.Empty(report.Find<BoundedStringBuiltinTruncationFinding>("BoundedStringBuiltinTruncationScanner"));
    }

    [Fact]
    public async Task LiveDeployment_SpaceCountPastCap_Fires()
    {
        var report = await EngineAuthoritativeScan.ScanAsync(
            """
            CREATE PROCEDURE dbo.usp_SpaceOverCap AS
            BEGIN
                SELECT SPACE(9000);
            END
            """,
            minimumConfidence: FindingConfidence.Low);

        var finding = Assert.Single(report.Find<BoundedStringBuiltinTruncationFinding>("BoundedStringBuiltinTruncationScanner"));
        Assert.Equal(BoundedStringBuiltinTruncationFindingKind.SpaceResultTruncated, finding.Kind);
        Assert.Equal("SPACE", finding.FunctionName);
        Assert.Equal(9000, finding.ComputedLength);
        Assert.Equal(8000, finding.CapBytes);
    }

    [Fact]
    public async Task LiveDeployment_SpaceCountWithinCap_NeverFires()
    {
        var report = await EngineAuthoritativeScan.ScanAsync(
            """
            CREATE PROCEDURE dbo.usp_SpaceWithinCap AS
            BEGIN
                SELECT SPACE(100);
            END
            """,
            minimumConfidence: FindingConfidence.Low);

        Assert.Empty(report.Find<BoundedStringBuiltinTruncationFinding>("BoundedStringBuiltinTruncationScanner"));
    }
}
