using SilentScan.Core.Diagnostics;
using SilentScan.Core.Reporting;

namespace SilentScan.Tests.Reporting;

public sealed class SkippedConstructSummaryTests
{
    [Fact]
    public void From_Empty_ReturnsZeroedSummary()
    {
        var summary = SkippedConstructSummary.From([]);

        Assert.Equal(0, summary.TotalCount);
        Assert.Empty(summary.CountsByConstructKind);
    }

    [Fact]
    public void From_MixedConstructKinds_CountsEachBucket()
    {
        var entries = new[]
        {
            new SkippedConstruct(AnalysisPass.Catalog, "a.sql", 1, 1, "column type", "reason 1"),
            new SkippedConstruct(AnalysisPass.Catalog, "a.sql", 2, 1, "column type", "reason 2"),
            new SkippedConstruct(AnalysisPass.Lineage, "a.sql", 3, 1, "view/TVF definer", "reason 3"),
            new SkippedConstruct(AnalysisPass.Predicates, "a.sql", 4, 1, "predicate operand", "reason 4"),
        };

        var summary = SkippedConstructSummary.From(entries);

        Assert.Equal(4, summary.TotalCount);
        Assert.Equal(new Dictionary<string, int> { ["column type"] = 2, ["view/TVF definer"] = 1, ["predicate operand"] = 1 }, summary.CountsByConstructKind);
    }
}
