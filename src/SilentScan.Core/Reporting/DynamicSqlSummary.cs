using SilentScan.Core.Predicates;

namespace SilentScan.Core.Reporting;

/// <summary>
/// Aggregates <see cref="DynamicSqlFinding"/>s into the "X% of dynamic SQL call sites we could
/// (not) analyze" figure CLAUDE.md's dynamic SQL policy requires the study to report - computed
/// once here rather than by hand, so every corpus rerun reports it the same way.
/// </summary>
public sealed record DynamicSqlSummary(
    int TotalCallSites,
    int AnalyzedCount,
    int UnanalyzableCount,
    int InnerParseFailedCount,
    IReadOnlyDictionary<string, int> UnanalyzableReasonCounts)
{
    /// <summary>Fraction of call sites proved constant and fully analyzed (Tiers A/B/C) - 0 for an empty corpus, never a division-by-zero surprise.</summary>
    public double AnalyzedFraction => TotalCallSites == 0 ? 0d : (double)AnalyzedCount / TotalCallSites;

    public static DynamicSqlSummary From(IReadOnlyList<DynamicSqlFinding> findings)
    {
        var analyzed = 0;
        var unanalyzable = 0;
        var innerParseFailed = 0;
        var reasonCounts = new Dictionary<string, int>(StringComparer.Ordinal);

        foreach (var finding in findings)
        {
            switch (finding.Outcome)
            {
                case DynamicSqlOutcome.AnalyzedLiteral:
                    analyzed++;
                    break;

                case DynamicSqlOutcome.Unanalyzable:
                    unanalyzable++;
                    var reason = finding.Reason ?? "unspecified";
                    reasonCounts[reason] = reasonCounts.GetValueOrDefault(reason) + 1;
                    break;

                case DynamicSqlOutcome.InnerParseFailed:
                    innerParseFailed++;
                    break;
            }
        }

        return new DynamicSqlSummary(findings.Count, analyzed, unanalyzable, innerParseFailed, reasonCounts);
    }
}
