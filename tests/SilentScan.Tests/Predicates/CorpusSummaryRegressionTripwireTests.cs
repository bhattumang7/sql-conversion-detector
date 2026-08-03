using SilentScan.Core.Parsing;
using SilentScan.Core.Reporting;
using SilentScan.Tests.Support;

namespace SilentScan.Tests.Predicates;

/// <summary>
/// A standing regression tripwire for the scan pipeline's SUMMARY-level numbers - distinct from
/// FullPipelineSyntheticMiniProjectTests' individual planted-finding assertions, which each
/// check ONE specific finding's own shape but never bundle every summary field together in one
/// place. A classification-logic change that shifts how many comparisons resolve to each verdict
/// (the kind of change task #8/#9's own type-inference work made earlier) could silently move
/// these numbers with no single existing test positioned to catch the shift, since every other
/// test only asserts a narrow slice of it. Deliberately targets the checked-in, fully synthetic
/// fixtures/mini_project/ - not the real pilot corpus (corpus/manifest.json's 5 repos): those
/// depend on an external clone step this project doesn't guarantee before `dotnet test` runs
/// (no clone provisioning is documented in docs/local-dev.md), and their own pinned commit SHAs
/// could themselves be bumped independently of any code change here, which would make a
/// real-corpus golden count drift for reasons having nothing to do with a regression. No live
/// database needed - this only asserts the STATIC summary shape, not oracle confirmation (that's
/// FullPipelineSyntheticMiniProjectTests' job, per-finding, already).
/// </summary>
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

        // TypedPredicateSummary - every classified comparison, verdict-bearing or not. Individual
        // findings behind these counts are each already named and oracle-confirmed in
        // FullPipelineSyntheticMiniProjectTests (DisplayName/ScanForced, Region/RangeSeek,
        // OrderCode/ScanForced depth 2, AccountCode/ScanForced via dynamic Tier C, Phone/ScanForced
        // via Tier B, Email/ScanForced via Tier A) - this only pins the BUNDLE staying the same
        // shape, not re-proving any one of them against the real engine again.
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

        // DynamicSqlSummary - the 5 EXEC/sp_executesql call sites the fixture plants (4
        // analyzable: literal/Tier B/Tier C/clean, 1 genuinely Unanalyzable: a procedure
        // parameter with no known caller in this fixture - value-seeding across proc-call edges
        // now reports its own honest reason rather than the generic "variable-not-in-scope" a
        // caller-blind lookup used to produce).
        var dynamicSql = report.DynamicSqlSummary;
        Assert.Equal(5, dynamicSql.TotalCallSites);
        Assert.Equal(4, dynamicSql.AnalyzedCount);
        Assert.Equal(1, dynamicSql.UnanalyzableCount);
        Assert.Equal(0, dynamicSql.InnerParseFailedCount);
        Assert.Equal(1, Assert.Contains("procedure-parameter:no-known-call-site", dynamicSql.UnanalyzableReasonCounts));

        // SkippedConstructSummary - the fixture's one deliberately-unresolvable comparison
        // (both sides non-column), ledgered rather than silently dropped.
        var skipped = report.SkippedConstructSummary;
        Assert.Equal(1, skipped.TotalCount);
        Assert.Equal(1, Assert.Contains("no column operand", skipped.CountsByConstructKind));

        // Findings-list counts - not duplicated logic with the summaries above (a
        // TypedFindings.Count that disagreed with RangeSeek+ScanForced+Unknown would itself be a
        // bug the OTHER mini_project test already guards - repeated here only as a cheap total
        // sanity check on every OTHER finding stream this fixture plants).
        Assert.Single(report.Tier1Findings);
        Assert.Equal(6, report.TypedFindings.Count);
        Assert.Empty(report.ExpressionDerivedFindings);
        Assert.Empty(report.CollationConflictFindings);
        Assert.Empty(report.WriteLossFindings);
        Assert.Equal(5, report.DynamicSqlFindings.Count);
        Assert.Single(report.SkippedConstructs);
    }
}
