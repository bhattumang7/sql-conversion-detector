using SilentScan.Core.Lineage;

namespace SilentScan.Core.Predicates;

public static class NestedViewDepthScanner
{
    public const int DepthThreshold = 2;

    public static IReadOnlyList<NestedViewDepthFinding> Scan(
        IReadOnlyDictionary<string, ViewExpansionOrigin> viewExpansionMap, IReadOnlyList<ViewDefinition> views)
    {
        var byName = new Dictionary<string, ViewDefinition>(StringComparer.OrdinalIgnoreCase);
        foreach (var view in views)
        {
            byName[view.QualifiedName] = view;
        }

        var findings = new List<NestedViewDepthFinding>();

        foreach (var (qualifiedName, origin) in viewExpansionMap)
        {
            if (origin.Depth < DepthThreshold || !byName.TryGetValue(qualifiedName, out var view))
            {
                continue;
            }

            findings.Add(new NestedViewDepthFinding(
                qualifiedName, origin.Depth, origin.Chain, [.. origin.BaseTables.OrderBy(t => t, StringComparer.Ordinal)],
                view.SourcePath, view.SourceLine));
        }

        return
        [
            .. findings
                .OrderByDescending(f => f.Depth)
                .ThenBy(f => f.ViewQualifiedName, StringComparer.Ordinal),
        ];
    }
}
