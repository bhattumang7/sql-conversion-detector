using SilentScan.Core.Catalog;
using SilentScan.Core.Common;

namespace SilentScan.Core.Lineage;

public sealed record ViewExpansionOrigin(
    int Depth,
    IReadOnlyList<string> Chain,
    IReadOnlySet<string> BaseTables,
    bool PartiallyUnexpanded);

public static class ViewExpansionMap
{
    public static IReadOnlyDictionary<string, ViewExpansionOrigin> Build(IReadOnlyList<ViewDefinition> views, DatabaseCatalog catalog)
    {
        var viewsByName = new Dictionary<string, ViewDefinition>(StringComparer.OrdinalIgnoreCase);
        foreach (var view in views)
        {

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

        return (NoBaseTables, true, null);
    }
}
