using Microsoft.SqlServer.TransactSql.ScriptDom;
using SilentScan.Core.Catalog;

namespace SilentScan.Core.Lineage;

/// <summary>
/// Pass 2 sibling: for every view/inline TVF, whether its own definition contains a
/// multi-statement/CLR TVF fence - directly, or inherited through however many further
/// view/iTVF layers (docs/detection-checklist.md "MSTVF hidden under a view / another TVF -
/// lineage depth + origin, the permissions function wrapped in a view case"). The call site
/// naming that view looks identical to any harmless view reference; only this map, built once
/// against the resolved catalog, says otherwise.
/// </summary>
public static class TvfFenceMap
{
    /// <param name="views">Every CREATE VIEW and inline-TVF definition seen (<see cref="ViewDefinitionExtractor.Extract"/>'s <c>Views</c> - a multi-statement/CLR TVF has no body here, since its opacity to the optimizer is exactly what's being detected).</param>
    /// <param name="catalog">Resolved catalog carrying every table-valued function's <see cref="TableValuedFunctionKind"/>.</param>
    public static IReadOnlyDictionary<string, TvfFenceOrigin> Build(IReadOnlyList<ViewDefinition> views, DatabaseCatalog catalog)
    {
        var viewsByName = new Dictionary<string, ViewDefinition>(StringComparer.OrdinalIgnoreCase);
        foreach (var view in views)
        {
            // Same "last definition wins" upsert as ViewDependencyGraph.TopologicalSort - a
            // repo can contain the same view name CREATEd more than once across its history.
            viewsByName[view.QualifiedName] = view;
        }

        var context = new ResolutionContext(
            viewsByName, catalog, new Dictionary<string, TvfFenceOrigin?>(StringComparer.OrdinalIgnoreCase), new HashSet<string>(StringComparer.OrdinalIgnoreCase));

        foreach (var view in viewsByName.Values)
        {
            Resolve(view, context);
        }

        return context.Resolved
            .Where(kv => kv.Value is not null)
            .ToDictionary(kv => kv.Key, kv => kv.Value!, StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>Bundles the context threaded through every recursive <see cref="Resolve"/> call - kept as one record so a helper split off to reduce cognitive complexity doesn't just turn into another long parameter list.</summary>
    private readonly record struct ResolutionContext(
        IReadOnlyDictionary<string, ViewDefinition> ViewsByName,
        DatabaseCatalog Catalog,
        Dictionary<string, TvfFenceOrigin?> Resolved,
        HashSet<string> InProgress);

    private static TvfFenceOrigin? Resolve(ViewDefinition view, ResolutionContext context)
    {
        if (context.Resolved.TryGetValue(view.QualifiedName, out var cached))
        {
            return cached;
        }

        // A cyclic view dependency resolves to no fence here rather than looping -
        // ViewDependencyGraph's own cyclic-view handling already reports the cycle itself as a
        // lineage problem; this map doesn't need to report it a second time.
        if (!context.InProgress.Add(view.QualifiedName))
        {
            return null;
        }

        var (functionRefs, namedRefs) = TvfReferenceWalker.CollectFromClauses(view.SelectStatement);
        var found = functionRefs.Select(f => TryResolveFunctionReference(view, f, context)).FirstOrDefault(o => o is not null)
            ?? namedRefs.Select(n => TryResolveNamedReference(n, context)).FirstOrDefault(o => o is not null);

        context.InProgress.Remove(view.QualifiedName);
        context.Resolved[view.QualifiedName] = found;
        return found;
    }

    /// <summary>A direct MSTVF/CLR reference fences immediately (depth 1, origin here); an inline TVF reference has no fence of its own but is itself a view-shaped definition, so it's walked recursively for one it might inherit. Null when the name doesn't resolve to a known table-valued function at all.</summary>
    private static TvfFenceOrigin? TryResolveFunctionReference(ViewDefinition view, TvfLeafReference functionRef, ResolutionContext context)
    {
        var qualifiedName = context.Catalog.ResolveSynonymName(SchemaObjectNameHelper.Qualify(functionRef.Reference.SchemaObject));
        if (!context.Catalog.TryGetTableValuedFunctionKind(qualifiedName, out var kind))
        {
            return null;
        }

        if (kind is TableValuedFunctionKind.MultiStatement or TableValuedFunctionKind.Clr)
        {
            return new TvfFenceOrigin(qualifiedName, kind, view.SourcePath, functionRef.Reference.StartLine, Depth: 1);
        }

        return context.ViewsByName.TryGetValue(qualifiedName, out var inlineTvfDefinition)
            ? Inherit(Resolve(inlineTvfDefinition, context))
            : null;
    }

    /// <summary>A plain table-name reference only ever inherits a fence (it can never BE one) - null when the name isn't a known view/iTVF, or that view/iTVF carries no fence of its own.</summary>
    private static TvfFenceOrigin? TryResolveNamedReference(NamedTableReference namedRef, ResolutionContext context)
    {
        var qualifiedName = context.Catalog.ResolveSynonymName(SchemaObjectNameHelper.Qualify(namedRef.SchemaObject));
        return context.ViewsByName.TryGetValue(qualifiedName, out var referencedView)
            ? Inherit(Resolve(referencedView, context))
            : null;
    }

    private static TvfFenceOrigin? Inherit(TvfFenceOrigin? origin) => origin is null ? null : origin with { Depth = origin.Depth + 1 };
}
