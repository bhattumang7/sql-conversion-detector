using SilentScan.Core.Predicates;
using SilentScan.Core.Rules;

namespace SilentScan.Core.Reporting;

public sealed record TypedPredicateSummary(
    int TotalClassified,
    int SeekPreservedCount,
    int RangeSeekCount,
    int ScanForcedCount,
    int UnknownCount,
    int OperandClashCount,
    int DistinctRangeSeekCount,
    int DistinctScanForcedCount,
    int DistinctTotalClassified)
{
    public static TypedPredicateSummary From(IReadOnlyList<TypedPredicateFinding> allFindingsBeforeFiltering)
    {
        var seekPreserved = 0;
        var rangeSeek = 0;
        var scanForced = 0;
        var unknown = 0;
        var operandClash = 0;

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
                case Verdict.OperandClash:
                    operandClash++;
                    break;
            }
        }

        var distinctRangeSeek = TypedFindingDeduplicator.Dedupe(
            [.. allFindingsBeforeFiltering.Where(f => f.Verdict == Verdict.RangeSeek)]).Count;
        var distinctScanForced = TypedFindingDeduplicator.Dedupe(
            [.. allFindingsBeforeFiltering.Where(f => f.Verdict == Verdict.ScanForced)]).Count;
        var distinctTotalClassified = TypedFindingDeduplicator.Dedupe(allFindingsBeforeFiltering).Count;

        return new TypedPredicateSummary(
            allFindingsBeforeFiltering.Count, seekPreserved, rangeSeek, scanForced, unknown, operandClash,
            distinctRangeSeek, distinctScanForced, distinctTotalClassified);
    }
}
