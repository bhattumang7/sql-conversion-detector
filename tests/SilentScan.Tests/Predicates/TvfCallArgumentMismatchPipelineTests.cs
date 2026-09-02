using SilentScan.Core.Predicates;
using SilentScan.Tests.Support;

namespace SilentScan.Tests.Predicates;

[Trait("Category", "Oracle")]
public sealed class TvfCallArgumentMismatchPipelineTests : OracleTestFixture
{
    protected override string DatabaseNameSeed => nameof(TvfCallArgumentMismatchPipelineTests);

    private const string Sql = """
        CREATE FUNCTION dbo.fn_ByCode (@Code VARCHAR(10))
        RETURNS TABLE
        AS
        RETURN (SELECT @Code AS Code);
        GO
        CREATE PROCEDURE dbo.usp_Caller
        AS
        BEGIN
            DECLARE @LocalCode NVARCHAR(10) = N'日本語abc';
            SELECT * FROM dbo.fn_ByCode(@LocalCode);
        END
        """;

    protected override string Ddl => Sql;

    [Fact]
    public async Task RealCallerAndInlineTvf_UnicodeMismatch_SurfacesThroughLiveEngineAuthoritativePipeline()
    {
        var report = await EngineAuthoritativeScan.ScanAsync(Sql);

        var finding = Assert.Single(report.Find<TvfCallArgumentMismatchFinding>("TvfCallArgumentMismatchScanner"));
        Assert.Equal("dbo.usp_Caller", finding.CallerScopeQualifiedName);
        Assert.Equal("dbo.fn_ByCode", finding.CalleeQualifiedName);
        Assert.Equal("@Code", finding.FormalParameterName);
        Assert.Equal("@LocalCode", finding.CallerExpressionDisplay);
        Assert.Equal(WriteLossKind.UnicodeToNonUnicodeReplacement, finding.Kind);
    }
}
