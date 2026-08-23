using SilentScan.Core.Predicates;

namespace SilentScan.Core.Rules;

public static class TvfFenceClassifier
{
    public static TvfFenceFindingKind ClassifyDirectReference(bool isCorrelated, bool isStandalone) => isCorrelated switch
    {
        true => TvfFenceFindingKind.CorrelatedApply,
        false when isStandalone => TvfFenceFindingKind.Standalone,
        _ => TvfFenceFindingKind.FromOrJoin,
    };
}
