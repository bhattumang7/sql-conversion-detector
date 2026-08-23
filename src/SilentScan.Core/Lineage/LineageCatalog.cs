using SilentScan.Core.Diagnostics;

namespace SilentScan.Core.Lineage;

public sealed class LineageCatalog(IReadOnlyDictionary<string, ResolvedRelation> relationsByQualifiedName, IReadOnlySet<string> cyclicViews, SkipLedger skipped)
{
    public IReadOnlySet<string> CyclicViews { get; } = cyclicViews;

public SkipLedger Skipped { get; } = skipped;

public IReadOnlyDictionary<string, ResolvedRelation> AllRelations => relationsByQualifiedName;

    public ResolvedRelation? Find(string qualifiedName) =>
        relationsByQualifiedName.GetValueOrDefault(qualifiedName);
}
