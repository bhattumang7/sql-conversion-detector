using SilentScan.Core.Predicates;

namespace SilentScan.Core.Reporting;

public sealed record DynamicSqlSummary(
    int TotalCallSites,
    int AnalyzedCount,
    int UnanalyzableCount,
    int InnerParseFailedCount,
    IReadOnlyDictionary<string, int> UnanalyzableReasonCounts,
    int PartiallyAnalyzedCount = 0)
{
    public double AnalyzedFraction => TotalCallSites == 0 ? 0d : (double)AnalyzedCount / TotalCallSites;

    public static DynamicSqlSummary From(IReadOnlyList<DynamicSqlFinding> findings)
    {

        var analyzedSites = new HashSet<(string SourcePath, int Line, int Column)>();
        var unanalyzableSites = new HashSet<(string SourcePath, int Line, int Column)>();
        var innerParseFailedSites = new HashSet<(string SourcePath, int Line, int Column)>();
        var partiallyAnalyzedSites = new HashSet<(string SourcePath, int Line, int Column)>();
        var reasonCounts = new Dictionary<string, int>(StringComparer.Ordinal);

        foreach (var finding in findings)
        {
            var site = (finding.SourcePath, finding.Line, finding.Column);
            switch (finding.Outcome)
            {
                case DynamicSqlOutcome.AnalyzedLiteral:
                    analyzedSites.Add(site);
                    break;

                case DynamicSqlOutcome.Unanalyzable:
                    if (unanalyzableSites.Add(site))
                    {
                        var reason = finding.Reason ?? "unspecified";
                        reasonCounts[reason] = reasonCounts.GetValueOrDefault(reason) + 1;
                    }

                    break;

                case DynamicSqlOutcome.InnerParseFailed:
                    innerParseFailedSites.Add(site);
                    break;

                case DynamicSqlOutcome.PartiallyAnalyzed:
                    partiallyAnalyzedSites.Add(site);
                    break;
            }
        }

        var totalSites = analyzedSites.Count + unanalyzableSites.Count + innerParseFailedSites.Count + partiallyAnalyzedSites.Count;
        return new DynamicSqlSummary(totalSites, analyzedSites.Count, unanalyzableSites.Count, innerParseFailedSites.Count, reasonCounts, partiallyAnalyzedSites.Count);
    }
}
