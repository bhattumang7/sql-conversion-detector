using SilentScan.Core.Parsing;
using SilentScan.Core.Predicates;
using SilentScan.Core.Reporting;
using SilentScan.Core.Rules;
using SilentScan.Tests.Support;

namespace SilentScan.Tests.Predicates;

[Trait("Category", "Oracle")]
public sealed class FullPipelineSyntheticMiniProjectTests : OracleTestFixture
{
    private static readonly string ProjectDir = Path.Combine(AppContext.BaseDirectory, "fixtures", "mini_project");

    private ScanReport _report = null!;

    protected override string DatabaseNameSeed => nameof(FullPipelineSyntheticMiniProjectTests);

    protected override string Ddl =>
        File.ReadAllText(Path.Combine(ProjectDir, "01_schema.sql")) + "\nGO\n" +
        File.ReadAllText(Path.Combine(ProjectDir, "02_views.sql"));

    public override async Task InitializeAsync()
    {
        await base.InitializeAsync();

        var files = SqlFileDiscovery.EnumerateSqlFiles(ProjectDir);
        _report = await EngineAuthoritativeScan.ScanFilesAsync(files, "SQL_Latin1_General_CP1_CI_AS");

        foreach (var fileHealth in _report.ParseHealth.Files)
        {
            Assert.Empty(fileHealth.Errors);
        }
    }

    [Fact]
    public async Task DirectTableScanForced_IsPlantedAndFound_OracleConfirmed()
    {
        var finding = Assert.Single(_report.TypedFindings, f => f.Column.ColumnName == "DisplayName" && f.Column.TableQualifiedName == "dbo.Users");

        Assert.Equal(Verdict.ScanForced, finding.Verdict);
        Assert.Equal(0, finding.Column.Depth);
        Assert.True(finding.Column.Indexed);
        Assert.Null(finding.DynamicSqlCallSite);

        var results = await PipelineOracleVerification.VerifyAsync(Options, DatabaseName, [finding]);
        PipelineOracleVerification.AssertAllConfirmed(results);
    }

    [Fact]
    public async Task WindowsCollationRangeSeek_IsPlantedAndFound_OracleConfirmed()
    {
        var finding = Assert.Single(_report.TypedFindings, f => f.Column.ColumnName == "Region");

        Assert.Equal(Verdict.RangeSeek, finding.Verdict);
        Assert.False(finding.Column.Indexed);

        var results = await PipelineOracleVerification.VerifyAsync(Options, DatabaseName, [finding]);
        PipelineOracleVerification.AssertAllConfirmed(results);
    }

    [Fact]
    public async Task DepthTwoThroughViewChain_IsPlantedAndFound_OracleConfirmed()
    {
        var finding = Assert.Single(_report.TypedFindings, f => f.Column.ColumnName == "OrderCode");

        Assert.Equal(Verdict.ScanForced, finding.Verdict);
        Assert.Equal(2, finding.Column.Depth);
        Assert.True(finding.Column.Indexed);

        var results = await PipelineOracleVerification.VerifyAsync(Options, DatabaseName, [finding]);
        PipelineOracleVerification.AssertAllConfirmed(results);
    }

    [Fact]
    public void CleanTwin_SameParamFamilyAndCollation_ProducesNoActionableFinding()
    {

        Assert.Single(_report.TypedFindings, f => f.Column.ColumnName == "DisplayName");
    }

    [Fact]
    public void TypedPredicateSummary_ReflectsEveryClassifiedComparisonNotJustSurvivors()
    {

        var summary = _report.TypedPredicateSummary;

        Assert.True(summary.SeekPreservedCount > 0);
        Assert.Equal(
            summary.SeekPreservedCount + summary.RangeSeekCount + summary.ScanForcedCount + summary.UnknownCount,
            summary.TotalClassified);
        Assert.Equal(summary.RangeSeekCount + summary.ScanForcedCount + summary.UnknownCount, _report.TypedFindings.Count);
    }

    [Fact]
    public void Tier1FunctionWrappedColumn_IsPlantedAndFound()
    {
        var finding = Assert.Single(_report.Tier1Findings);

        Assert.Equal(SargabilityFindingKind.DateFunctionOnColumn, finding.Kind);
        Assert.Equal("CreatedAt", finding.ColumnName);
        Assert.Equal("YEAR", finding.Detail);

        Assert.Equal("dbo.Users", finding.TableQualifiedName);
        Assert.False(finding.Indexed);
    }

    [Fact]
    public void Tier1CleanTwin_SargableDateRange_ProducesNoFinding()
    {

        Assert.Single(_report.Tier1Findings);
    }

    [Fact]
    public void DynamicSqlUnanalyzable_IsPlantedAndFound()
    {

        var finding = Assert.Single(_report.DynamicSqlFindings, f => f.Outcome == DynamicSqlOutcome.Unanalyzable);

        Assert.Equal("symbolic-value-not-positionable:whole-statement", finding.Reason);
    }

    [Fact]
    public void DynamicSqlLiteralOnly_NoInnerFinding_IsAnalyzedWithNoFurtherFindings()
    {

        var analyzed = _report.DynamicSqlFindings.Where(f => f.Outcome == DynamicSqlOutcome.AnalyzedLiteral).ToList();

        Assert.Equal(4, analyzed.Count);
    }

    [Fact]
    public void DynamicSqlCleanTwin_OrdinaryProcCall_ProducesNoFinding()
    {

        Assert.Equal(5, _report.DynamicSqlFindings.Count);
    }

    [Fact]
    public async Task DynamicSqlTierCAccumulated_IsPlantedAndFound_OracleConfirmed()
    {

        var finding = Assert.Single(_report.TypedFindings, f => f.Column.ColumnName == "AccountCode");

        Assert.Equal(Verdict.ScanForced, finding.Verdict);
        Assert.True(finding.Column.Indexed);
        Assert.Equal("dbo.usp_DynamicTierCAccumulated_Fires", finding.SourcePath);
        Assert.Equal(5, finding.Line);
        Assert.NotNull(finding.DynamicSqlCallSite);
        Assert.Equal(6, finding.DynamicSqlCallSite!.Value.Line);

        var results = await PipelineOracleVerification.VerifyAsync(Options, DatabaseName, [finding]);
        PipelineOracleVerification.AssertAllConfirmed(results);
    }

    [Fact]
    public async Task DynamicSqlSpExecuteSqlDeclaredParam_TierB_IsPlantedAndFound_OracleConfirmed()
    {

        var finding = Assert.Single(_report.TypedFindings, f => f.Column.ColumnName == "Phone");

        Assert.Equal(Verdict.ScanForced, finding.Verdict);
        Assert.True(finding.Column.Indexed);
        Assert.NotNull(finding.DynamicSqlCallSite);
        Assert.Equal("dbo.usp_DynamicSpExecuteSqlDeclaredParam_Fires", finding.DynamicSqlCallSite!.Value.SourcePath);

        var results = await PipelineOracleVerification.VerifyAsync(Options, DatabaseName, [finding]);
        PipelineOracleVerification.AssertAllConfirmed(results);
    }

    [Fact]
    public async Task DynamicSqlLiteral_InnerPredicateIsReparsedAndRemappedToSourceLine_OracleConfirmed()
    {

        var finding = Assert.Single(_report.TypedFindings, f => f.Column.ColumnName == "Email");

        Assert.Equal(Verdict.ScanForced, finding.Verdict);
        Assert.True(finding.Column.Indexed);
        Assert.Equal("dbo.usp_DynamicLiteralWithFinding_Fires", finding.SourcePath);
        Assert.Equal(4, finding.Line);
        Assert.NotNull(finding.DynamicSqlCallSite);
        Assert.Equal("dbo.usp_DynamicLiteralWithFinding_Fires", finding.DynamicSqlCallSite!.Value.SourcePath);
        Assert.Equal(4, finding.DynamicSqlCallSite.Value.Line);

        var results = await PipelineOracleVerification.VerifyAsync(Options, DatabaseName, [finding]);
        PipelineOracleVerification.AssertAllConfirmed(results);
    }
}
