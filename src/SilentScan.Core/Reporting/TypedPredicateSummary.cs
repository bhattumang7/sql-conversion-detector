using SilentScan.Core.Predicates;
using SilentScan.Core.Rules;

namespace SilentScan.Core.Reporting;

/// <summary>
/// Counts every column-vs-other comparison Pass 3/4 classified, by verdict, BEFORE
/// <see cref="ScanReportBuilder"/> drops <see cref="Verdict.SeekPreserved"/> findings from
/// <see cref="ScanReport.TypedFindings"/>. Without this, a report has no denominator: it can
/// say "N ScanForced findings" but never "N ScanForced out of M comparisons classified", which
/// is the base rate an honest prevalence claim actually needs. SeekPreserved findings
/// themselves are still discarded (CLAUDE.md: only actionable findings are worth carrying
/// individually) - only their count survives, here.
///
/// Also carries the DISTINCT count for each actionable verdict (<see
/// cref="TypedFindingDeduplicator"/>): a corpus that re-issues the same CREATE PROCEDURE across
/// many incremental upgrade scripts (DNN Platform's 291 .SqlDataProvider files being the
/// concrete case that surfaced this) produces one raw finding per textual occurrence, which
/// inflates a prevalence count against "how many distinct bugs exist" by however many times the
/// repo's own history happened to repeat that file. Both numbers are kept - occurrence count is
/// still real data (it says something about how deeply the bug is baked into the repo's
/// history) - but a study reporting prevalence should lead with the distinct count.
/// </summary>
public sealed record TypedPredicateSummary(
    int TotalClassified,
    int SeekPreservedCount,
    int RangeSeekCount,
    int ScanForcedCount,
    int UnknownCount,
    int DistinctRangeSeekCount,
    int DistinctScanForcedCount)
{
    public static TypedPredicateSummary From(IReadOnlyList<TypedPredicateFinding> allFindingsBeforeFiltering)
    {
        var seekPreserved = 0;
        var rangeSeek = 0;
        var scanForced = 0;
        var unknown = 0;

        foreach (var finding in allFindingsBeforeFiltering)
        {
            switch (finding.Verdict)
            {
                case Verdict.SeekPreserved:
                    seekPreserved++;
                    break;
                case Verdict.RangeSeek:
                    rangeSeek++;
                    break;
                case Verdict.ScanForced:
                    scanForced++;
                    break;
                case Verdict.Unknown:
                    unknown++;
                    break;
            }
        }

        var distinctRangeSeek = TypedFindingDeduplicator.Dedupe(
            [.. allFindingsBeforeFiltering.Where(f => f.Verdict == Verdict.RangeSeek)]).Count;
        var distinctScanForced = TypedFindingDeduplicator.Dedupe(
            [.. allFindingsBeforeFiltering.Where(f => f.Verdict == Verdict.ScanForced)]).Count;

        return new TypedPredicateSummary(
            allFindingsBeforeFiltering.Count, seekPreserved, rangeSeek, scanForced, unknown,
            distinctRangeSeek, distinctScanForced);
    }
}
