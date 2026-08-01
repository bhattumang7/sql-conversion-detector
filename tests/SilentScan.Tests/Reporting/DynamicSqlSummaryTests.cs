using SilentScan.Core.Predicates;
using SilentScan.Core.Reporting;

namespace SilentScan.Tests.Reporting;

public sealed class DynamicSqlSummaryTests
{
    [Fact]
    public void From_EmptyFindings_ReturnsZeroedSummaryWithNoDivisionByZero()
    {
        var summary = DynamicSqlSummary.From([]);

        Assert.Equal(0, summary.TotalCallSites);
        Assert.Equal(0, summary.AnalyzedCount);
        Assert.Equal(0d, summary.AnalyzedFraction);
        Assert.Empty(summary.UnanalyzableReasonCounts);
    }

    [Fact]
    public void From_MixedOutcomes_CountsEachBucket()
    {
        var findings = new[]
        {
            new DynamicSqlFinding("a.sql", 1, 1, DynamicSqlOutcome.AnalyzedLiteral, null),
            new DynamicSqlFinding("a.sql", 2, 1, DynamicSqlOutcome.AnalyzedLiteral, null),
            new DynamicSqlFinding("a.sql", 3, 1, DynamicSqlOutcome.Unanalyzable, "undeclared-variable"),
            new DynamicSqlFinding("a.sql", 4, 1, DynamicSqlOutcome.Unanalyzable, "undeclared-variable"),
            new DynamicSqlFinding("a.sql", 5, 1, DynamicSqlOutcome.Unanalyzable, "goto-or-label-in-scope"),
            new DynamicSqlFinding("a.sql", 6, 1, DynamicSqlOutcome.InnerParseFailed, "Incorrect syntax."),
        };

        var summary = DynamicSqlSummary.From(findings);

        Assert.Equal(6, summary.TotalCallSites);
        Assert.Equal(2, summary.AnalyzedCount);
        Assert.Equal(3, summary.UnanalyzableCount);
        Assert.Equal(1, summary.InnerParseFailedCount);
        Assert.Equal(new Dictionary<string, int> { ["undeclared-variable"] = 2, ["goto-or-label-in-scope"] = 1 }, summary.UnanalyzableReasonCounts);
        Assert.Equal(2d / 6d, summary.AnalyzedFraction);
    }

    [Fact]
    public void From_UnanalyzableWithNullReason_GroupsUnderUnspecified()
    {
        var findings = new[] { new DynamicSqlFinding("a.sql", 1, 1, DynamicSqlOutcome.Unanalyzable, null) };

        var summary = DynamicSqlSummary.From(findings);

        Assert.Equal(1, summary.UnanalyzableReasonCounts["unspecified"]);
    }
}
