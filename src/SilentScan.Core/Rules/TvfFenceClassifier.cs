using SilentScan.Core.Predicates;

namespace SilentScan.Core.Rules;

/// <summary>
/// Pure decision for which <see cref="TvfFenceFindingKind"/> a direct table-valued-function
/// reference is, extracted out of <c>TvfFenceScanner</c>'s visitor (docs/detection-checklist.md
/// "Engineering debt" - separating rule decisions from ScriptDom traversal mechanics). Recognizing
/// whether the reference is correlated (an argument references an outer-query column) and whether
/// it sits standalone in the FROM clause (vs. joined) stays the caller's own AST-walking concern;
/// this only decides what those already-recognized facts mean.
/// </summary>
public static class TvfFenceClassifier
{
    public static TvfFenceFindingKind ClassifyDirectReference(bool isCorrelated, bool isStandalone) => isCorrelated switch
    {
        true => TvfFenceFindingKind.CorrelatedApply,
        false when isStandalone => TvfFenceFindingKind.Standalone,
        _ => TvfFenceFindingKind.FromOrJoin,
    };
}
