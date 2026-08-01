using SilentScan.Core.Diagnostics;

namespace SilentScan.Core.Lineage;

/// <summary>Pass 2 output: every view/inline-TVF/multi-statement-TVF, resolved to its output columns' provenance.</summary>
public sealed class LineageCatalog(IReadOnlyDictionary<string, ResolvedRelation> relationsByQualifiedName, IReadOnlySet<string> cyclicViews, SkipLedger skipped)
{
    public IReadOnlySet<string> CyclicViews { get; } = cyclicViews;

    /// <summary>Everything Pass 2 saw but could not resolve - never silently dropped.</summary>
    public SkipLedger Skipped { get; } = skipped;

    /// <summary>All resolved views/TVFs, for callers (e.g. Pass 3's predicate extractor) that need to build their own FROM scopes against them.</summary>
    public IReadOnlyDictionary<string, ResolvedRelation> AllRelations => relationsByQualifiedName;

    public ResolvedRelation? Find(string qualifiedName) =>
        relationsByQualifiedName.GetValueOrDefault(qualifiedName);
}
