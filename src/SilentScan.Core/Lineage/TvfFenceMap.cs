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

        var resolved = new Dictionary<string, TvfFenceOrigin?>(StringComparer.OrdinalIgnoreCase);
        var inProgress = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var view in viewsByName.Values)
        {
            Resolve(view, viewsByName, catalog, resolved, inProgress);
        }

        return resolved
            .Where(kv => kv.Value is not null)
            .ToDictionary(kv => kv.Key, kv => kv.Value!, StringComparer.OrdinalIgnoreCase);
    }

    private static TvfFenceOrigin? Resolve(
        ViewDefinition view,
        IReadOnlyDictionary<string, ViewDefinition> viewsByName,
        DatabaseCatalog catalog,
        Dictionary<string, TvfFenceOrigin?> resolved,
        HashSet<string> inProgress)
    {
        if (resolved.TryGetValue(view.QualifiedName, out var cached))
        {
            return cached;
        }

        // A cyclic view dependency resolves to no fence here rather than looping -
        // ViewDependencyGraph's own cyclic-view handling already reports the cycle itself as a
        // lineage problem; this map doesn't need to report it a second time.
        if (!inProgress.Add(view.QualifiedName))
        {
            return null;
        }

        var (functionRefs, namedRefs) = TvfReferenceWalker.CollectFromClauses(view.SelectStatement);

        TvfFenceOrigin? found = null;

        foreach (var functionRef in functionRefs)
        {
            var qualifiedName = catalog.ResolveSynonymName(SchemaObjectNameHelper.Qualify(functionRef.Reference.SchemaObject));
            if (!catalog.TryGetTableValuedFunctionKind(qualifiedName, out var kind))
            {
                continue;
            }

            if (kind is TableValuedFunctionKind.MultiStatement or TableValuedFunctionKind.Clr)
            {
                found = new TvfFenceOrigin(qualifiedName, kind, view.SourcePath, functionRef.Reference.StartLine, Depth: 1);
                break;
            }

            // kind == Inline: no fence of its own, but it's a view-shaped definition just like
            // any NamedTableReference target, so it still needs to be walked for an inherited
            // fence below.
            if (viewsByName.TryGetValue(qualifiedName, out var inlineTvfDefinition))
            {
                var inherited = Resolve(inlineTvfDefinition, viewsByName, catalog, resolved, inProgress);
                if (inherited is not null)
                {
                    found = inherited with { Depth = inherited.Depth + 1 };
                    break;
                }
            }
        }

        if (found is null)
        {
            foreach (var namedRef in namedRefs)
            {
                var qualifiedName = catalog.ResolveSynonymName(SchemaObjectNameHelper.Qualify(namedRef.SchemaObject));
                if (!viewsByName.TryGetValue(qualifiedName, out var referencedView))
                {
                    continue;
                }

                var inherited = Resolve(referencedView, viewsByName, catalog, resolved, inProgress);
                if (inherited is not null)
                {
                    found = inherited with { Depth = inherited.Depth + 1 };
                    break;
                }
            }
        }

        inProgress.Remove(view.QualifiedName);
        resolved[view.QualifiedName] = found;
        return found;
    }
}
