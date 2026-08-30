using SilentScan.Core.Predicates;
using SilentScan.Tests.Support;

namespace SilentScan.Tests.Predicates;

[Trait("Category", "Oracle")]
public sealed class ProcCallArgumentMismatchPipelineTests : OracleTestFixture
{
    protected override string DatabaseNameSeed => nameof(ProcCallArgumentMismatchPipelineTests);

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

        var finding = Assert.Single(report.Find<ProcCallArgumentMismatchFinding>("ProcCallArgumentMismatchScanner"));
        Assert.Equal("dbo.usp_Caller", finding.CallerScopeQualifiedName);
        Assert.Equal("dbo.usp_Callee", finding.CalleeQualifiedName);
        Assert.Equal("@Code", finding.FormalParameterName);
        Assert.Equal("@LocalCode", finding.CallerExpressionDisplay);
        Assert.Equal(WriteLossKind.UnicodeToNonUnicodeReplacement, finding.Kind);
        Assert.False(finding.IsOutputWriteback);
    }
}
