using SilentScan.Core.Parsing;
using SilentScan.Core.Reporting;
using SilentScan.Tests.Support;
using SilentScan.Core.Predicates;

namespace SilentScan.Tests.Predicates;

[Trait("Category", "Oracle")]
public sealed class CorpusSummaryRegressionTripwireTests
{
    private static readonly string ProjectDir = Path.Combine(AppContext.BaseDirectory, "fixtures", "mini_project");

    [Fact]
    public async Task MiniProject_SummaryCounts_MatchTheKnownGoldenShape()
    {
        var files = SqlFileDiscovery.EnumerateSqlFiles(ProjectDir);
        var report = await EngineAuthoritativeScan.ScanFilesAsync(files, "SQL_Latin1_General_CP1_CI_AS");

        foreach (var fileHealth in report.ParseHealth.Files)
        {
            Assert.Empty(fileHealth.Errors);
        }

        var typed = report.TypedPredicateSummary;
        Assert.Equal(9, typed.TotalClassified);
        Assert.Equal(3, typed.SeekPreservedCount);
        Assert.Equal(1, typed.RangeSeekCount);
        Assert.Equal(5, typed.ScanForcedCount);
        Assert.Equal(0, typed.UnknownCount);
        Assert.Equal(0, typed.OperandClashCount);
        Assert.Equal(1, typed.DistinctRangeSeekCount);
        Assert.Equal(5, typed.DistinctScanForcedCount);
        Assert.Equal(9, typed.DistinctTotalClassified);

        var dynamicSql = report.DynamicSqlSummary;
        Assert.Equal(5, dynamicSql.TotalCallSites);
        Assert.Equal(4, dynamicSql.AnalyzedCount);
        Assert.Equal(1, dynamicSql.UnanalyzableCount);
        Assert.Equal(0, dynamicSql.InnerParseFailedCount);
        Assert.Equal(1, Assert.Contains("symbolic-value-not-positionable:whole-statement", dynamicSql.UnanalyzableReasonCounts));

        var skipped = report.SkippedConstructSummary;
        Assert.Equal(6, skipped.TotalCount);
        Assert.Equal(1, Assert.Contains("no column operand", skipped.CountsByConstructKind));
        Assert.Equal(5, Assert.Contains("procedure call graph edge", skipped.CountsByConstructKind));

        Assert.Single(report.Find<SargabilityFinding>("NonSargablePredicateScanner"));
        Assert.Equal(6, report.Find<TypedPredicateFinding>("TypedPredicateExtractor").Count);
        Assert.Empty(report.Find<ExpressionDerivedFinding>("TypedPredicateExtractor"));
        Assert.Empty(report.Find<CollationConflictFinding>("TypedPredicateExtractor"));
        Assert.Empty(report.Find<WriteLossFinding>("TypedPredicateExtractor"));
        Assert.Equal(5, report.Find<DynamicSqlFinding>("DynamicSqlScanner").Count);
        Assert.Equal(6, report.SkippedConstructs.Count);
    }
}
