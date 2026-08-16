using SilentScan.Core.Catalog;

namespace SilentScan.Core.Lineage;

/// <summary>
/// One view/inline-TVF's transitive shape: how many view/TVF layers deep it nests
/// (<see cref="Depth"/>), the chain of names from itself down to the deepest nested view
/// (<see cref="Chain"/>, top-down), and every DISTINCT base table it transitively bottoms out at
/// (<see cref="BaseTables"/>) - the "expanded" count a written FROM-clause reference to this view
/// actually costs, as opposed to the "1" its own written reference suggests.
/// <see cref="PartiallyUnexpanded"/> is true when some reference inside this view's own body (or
/// an ancestor's) could not be resolved further - an MSTVF/CLR TVF fence, a dynamic construct, or
/// unmodeled DDL - so <see cref="BaseTables"/> is a lower bound, never claimed as exhaustive.
/// </summary>
public sealed record ViewExpansionOrigin(
    int Depth,
    IReadOnlyList<string> Chain,
    IReadOnlySet<string> BaseTables,
    bool PartiallyUnexpanded);

/// <summary>
/// docs/detection-checklist.md Tier 2 "Lineage-metric findings" - shared foundation for "Nested-
/// view depth report" and "Post-expansion join width": both need the same transitive walk through
/// every view/inline-TVF's own dependencies, computed once and memoized, exactly the pattern
/// <see cref="TvfFenceMap"/> already established for the same "walk once, memoize, reuse" shape.
/// "View" here means both CREATE VIEW and inline TVF uniformly, matching <see cref="ViewDefinition"/>'s
/// own established "Inline TVFs = views" treatment elsewhere in this codebase.
/// </summary>
public static class ViewExpansionMap
{
    public static IReadOnlyDictionary<string, ViewExpansionOrigin> Build(IReadOnlyList<ViewDefinition> views, DatabaseCatalog catalog)
    {
        var viewsByName = new Dictionary<string, ViewDefinition>(StringComparer.OrdinalIgnoreCase);
        foreach (var view in views)
        {
            // Same "last definition wins" upsert as ViewDependencyGraph.TopologicalSort/TvfFenceMap.
            viewsByName[view.QualifiedName] = view;
        }

        var context = new ResolutionContext(
            viewsByName, catalog, new Dictionary<string, ViewExpansionOrigin?>(StringComparer.OrdinalIgnoreCase), new HashSet<string>(StringComparer.OrdinalIgnoreCase));

        foreach (var view in viewsByName.Values)
        {
            Resolve(view, context);
        }

        return context.Resolved
            .Where(kv => kv.Value is not null)
            .ToDictionary(kv => kv.Key, kv => kv.Value!, StringComparer.OrdinalIgnoreCase);
    }

    private readonly record struct ResolutionContext(
        IReadOnlyDictionary<string, ViewDefinition> ViewsByName,
        DatabaseCatalog Catalog,
        Dictionary<string, ViewExpansionOrigin?> Resolved,
        HashSet<string> InProgress);

    private static ViewExpansionOrigin? Resolve(ViewDefinition view, ResolutionContext context)
    {
        if (context.Resolved.TryGetValue(view.QualifiedName, out var cached))
        {
            return cached;
        }

        // A cyclic dependency resolves to no expansion here rather than looping -
        // ViewDependencyGraph's own cyclic-view handling already reports the cycle itself as a
        // lineage problem; this map doesn't need to report it a second time.
        if (!context.InProgress.Add(view.QualifiedName))
        {
            return null;
        }

        var (functionRefs, namedRefs) = TvfReferenceWalker.CollectFromClauses(view.SelectStatement);
        var referencedNames = functionRefs.Select(f => context.Catalog.ResolveSynonymName(SchemaObjectNameHelper.Qualify(f.Reference.SchemaObject)))
            .Concat(namedRefs.Select(n => context.Catalog.ResolveSynonymName(SchemaObjectNameHelper.Qualify(n.SchemaObject))))
            .Distinct(StringComparer.OrdinalIgnoreCase);

        var baseTables = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var partiallyUnexpanded = false;
        var deepestChildDepth = -1;
        string? deepestChildName = null;
        ViewExpansionOrigin? deepestChildOrigin = null;

        foreach (var qualifiedName in referencedNames)
        {
            var (refBaseTables, refPartiallyUnexpanded, child) = ResolveReference(qualifiedName, context);
            baseTables.UnionWith(refBaseTables);
            partiallyUnexpanded |= refPartiallyUnexpanded;

            if (child is { } named && named.Origin.Depth > deepestChildDepth)
            {
                deepestChildDepth = named.Origin.Depth;
                deepestChildName = named.Name;
                deepestChildOrigin = named.Origin;
            }
        }

        var depth = deepestChildDepth + 1;
        var chain = deepestChildName is null
            ? (IReadOnlyList<string>)[view.QualifiedName]
            : [view.QualifiedName, .. deepestChildOrigin!.Chain];

        var result = new ViewExpansionOrigin(depth, chain, baseTables, partiallyUnexpanded);
        context.InProgress.Remove(view.QualifiedName);
        context.Resolved[view.QualifiedName] = result;
        return result;
    }

    private static readonly IReadOnlySet<string> NoBaseTables = new HashSet<string>();

    /// <summary>One referenced name's own contribution to the parent's aggregate: its transitive base tables, whether it (or anything below it) is partially unexpanded, and - only for a child view/TVF reference, never a base table - its own resolved origin, so the caller can track the deepest child without re-deriving depth here.</summary>
    private static (IReadOnlySet<string> BaseTables, bool PartiallyUnexpanded, (string Name, ViewExpansionOrigin Origin)? Child) ResolveReference(
        string qualifiedName, ResolutionContext context)
    {
        if (context.ViewsByName.TryGetValue(qualifiedName, out var childView))
        {
            var childOrigin = Resolve(childView, context);
            return childOrigin is null
                ? (NoBaseTables, true, null)
                : (childOrigin.BaseTables, childOrigin.PartiallyUnexpanded, (qualifiedName, childOrigin));
        }

        if (context.Catalog.Find(qualifiedName) is { Kind: CatalogTableKind.Table or CatalogTableKind.ClrTableValuedFunction })
        {
            return (new HashSet<string>(StringComparer.OrdinalIgnoreCase) { qualifiedName }, false, null);
        }

        // Unresolved: an MSTVF fence, a dynamic/unmodeled construct, or a temp table/table
        // variable - none contribute a countable base table, and none can be expanded further.
        // Never guessed at.
        return (NoBaseTables, true, null);
    }
}
