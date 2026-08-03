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

    [Fact]
    public void From_MultipleAssembliesFromSameCallSite_CountsOneCallSiteNotOnePerAssembly()
    {
        // Branch-fold coverage (roadmap "trace dynamic SQL across IF/ELSE branches") reports one
        // AnalyzedLiteral finding per possible constant assembly - all sharing the same
        // (SourcePath, Line, Column) call site. The summary must count distinct call sites, not
        // raw findings, or "% of call sites analyzed" would be inflated by however many
        // assemblies a single site happened to fold to.
        var findings = new[]
        {
            new DynamicSqlFinding("a.sql", 10, 5, DynamicSqlOutcome.AnalyzedLiteral, null),
            new DynamicSqlFinding("a.sql", 10, 5, DynamicSqlOutcome.AnalyzedLiteral, null),
            new DynamicSqlFinding("a.sql", 10, 5, DynamicSqlOutcome.AnalyzedLiteral, null),
            new DynamicSqlFinding("a.sql", 20, 5, DynamicSqlOutcome.Unanalyzable, "undeclared-variable"),
        };

        var summary = DynamicSqlSummary.From(findings);

        Assert.Equal(2, summary.TotalCallSites);
        Assert.Equal(1, summary.AnalyzedCount);
        Assert.Equal(1, summary.UnanalyzableCount);
        Assert.Equal(1, summary.UnanalyzableReasonCounts["undeclared-variable"]);
        Assert.Equal(0.5d, summary.AnalyzedFraction);
    }

    [Fact]
    public void From_RepeatedUnanalyzableAtSameCallSite_ReasonCountedOncePerSite()
    {
        // Guards the reason-count grouping specifically: even if the same call site somehow
        // reports Unanalyzable more than once, its reason must not be double-counted.
        var findings = new[]
        {
            new DynamicSqlFinding("a.sql", 10, 5, DynamicSqlOutcome.Unanalyzable, "goto-or-label-in-scope"),
            new DynamicSqlFinding("a.sql", 10, 5, DynamicSqlOutcome.Unanalyzable, "goto-or-label-in-scope"),
        };

        var summary = DynamicSqlSummary.From(findings);

        Assert.Equal(1, summary.TotalCallSites);
        Assert.Equal(1, summary.UnanalyzableReasonCounts["goto-or-label-in-scope"]);
    }
}
