using SilentScan.Core.Diagnostics;

namespace SilentScan.Core.Reporting;

public sealed record SkippedConstructSummary(
    int TotalCount,
    IReadOnlyDictionary<string, int> CountsByConstructKind)
{
    public static SkippedConstructSummary From(IReadOnlyList<SkippedConstruct> skippedConstructs)
    {
        var counts = skippedConstructs
            .GroupBy(entry => entry.ConstructKind, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.Count(), StringComparer.Ordinal);

        return new SkippedConstructSummary(skippedConstructs.Count, counts);
    }
}
