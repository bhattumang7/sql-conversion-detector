using SilentScan.Core.Predicates;
using SilentScan.Tests.Support;

namespace SilentScan.Tests.Predicates;

/// <summary>
/// End-to-end proof (docs/detection-checklist.md Tier 1 "call-boundary argument mismatch") that
/// a real deployed caller/callee pair surfaces through the SAME live/engine-authoritative
/// pipeline production uses (<see cref="EngineAuthoritativeScan"/>) - not just that the scanner's
/// own unit logic is correct against a hand-built graph
/// (<see cref="ProcCallArgumentMismatchScannerTests"/>). The underlying silent-data-loss runtime
/// behavior each <see cref="Rules.WriteLossClassifier"/> kind claims is already oracle-proven by
/// self-authored probe rows in <see cref="WriteLossOracleTests"/> - reusing that classifier here
/// means this test only needs to prove the call-boundary WIRING (caller variable type resolution
/// through the real catalog, matched against the real declared parameter type), not re-prove the
/// runtime fact a second time.
/// </summary>
[Trait("Category", "Oracle")]
public sealed class ProcCallArgumentMismatchPipelineTests : OracleTestFixture
{
    protected override string DatabaseNameSeed => nameof(ProcCallArgumentMismatchPipelineTests);

    // The callee's own parameter is the NARROWER, non-unicode side (VARCHAR) and the caller's
    // variable is the WIDER, unicode side (NVARCHAR) - the lossy direction. The reverse (a
    // non-unicode source into a unicode target) is safe/widening, not lossy - confirmed the hard
    // way while authoring this fixture: an initial draft had the two swapped and produced zero
    // findings, which is what WriteLossClassifier is correct to report for that direction.
    private const string Sql = """
        CREATE PROCEDURE dbo.usp_Callee @Code VARCHAR(20)
        AS
        BEGIN
            SELECT @Code;
        END
        GO
        CREATE PROCEDURE dbo.usp_Caller
        AS
        BEGIN
            DECLARE @LocalCode NVARCHAR(20) = N'日本語abc';
            EXEC dbo.usp_Callee @LocalCode;
        END
        """;

    protected override string Ddl => Sql;

    [Fact]
    public async Task RealCallerCalleePair_UnicodeMismatch_SurfacesThroughLiveEngineAuthoritativePipeline()
    {
        var report = await EngineAuthoritativeScan.ScanAsync(Sql);

        var finding = Assert.Single(report.ProcCallArgumentMismatchFindings);
        Assert.Equal("dbo.usp_Caller", finding.CallerScopeQualifiedName);
        Assert.Equal("dbo.usp_Callee", finding.CalleeQualifiedName);
        Assert.Equal("@Code", finding.FormalParameterName);
        Assert.Equal("@LocalCode", finding.CallerVariableName);
        Assert.Equal(WriteLossKind.UnicodeToNonUnicodeReplacement, finding.Kind);
    }
}
