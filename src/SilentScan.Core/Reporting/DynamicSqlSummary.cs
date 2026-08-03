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
        // Branch-fold coverage (roadmap "trace dynamic SQL across IF/ELSE/TRY-CATCH branches")
        // can report several AnalyzedLiteral findings for ONE call site - one per possible
        // constant assembly, all sharing the same (SourcePath, Line, Column). Counting by
        // distinct call site rather than by raw finding keeps "% of call sites analyzed" honest
        // - a site with three assemblies must count once here, not three times.
        var analyzedSites = new HashSet<(string SourcePath, int Line, int Column)>();
        var unanalyzableSites = new HashSet<(string SourcePath, int Line, int Column)>();
        var innerParseFailedSites = new HashSet<(string SourcePath, int Line, int Column)>();
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
            }
        }

        var totalSites = analyzedSites.Count + unanalyzableSites.Count + innerParseFailedSites.Count;
        return new DynamicSqlSummary(totalSites, analyzedSites.Count, unanalyzableSites.Count, innerParseFailedSites.Count, reasonCounts);
    }
}
