using Microsoft.SqlServer.TransactSql.ScriptDom;
using SilentScan.Core.Catalog;
using SilentScan.Core.Common;

namespace SilentScan.Core.Lineage;

public static class TvfFenceMap
{
    public static IReadOnlyDictionary<string, TvfFenceOrigin> Build(IReadOnlyList<ViewDefinition> views, DatabaseCatalog catalog)
    {
        var viewsByName = new Dictionary<string, ViewDefinition>(catalog.IdentifierComparer);
        foreach (var view in views)
        {

            viewsByName[view.QualifiedName] = view;
        }

        var context = new ResolutionContext(
            viewsByName, catalog, new Dictionary<string, TvfFenceOrigin?>(catalog.IdentifierComparer), new HashSet<string>(catalog.IdentifierComparer));

        foreach (var view in viewsByName.Values)
        {
            Resolve(view, context);
        }

        return context.Resolved
            .Where(kv => kv.Value is not null)
            .ToDictionary(kv => kv.Key, kv => kv.Value!, catalog.IdentifierComparer);
    }

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

        if (!context.InProgress.Add(view.QualifiedName))
        {
            return null;
        }

        var (functionRefs, namedRefs) = TvfReferenceWalker.CollectFromClauses(view.SelectStatement, context.Catalog.IdentifierComparer);
        var found = functionRefs.Select(f => TryResolveFunctionReference(view, f, context)).FirstOrDefault(o => o is not null)
            ?? namedRefs.Select(n => TryResolveNamedReference(n, context)).FirstOrDefault(o => o is not null);

        context.InProgress.Remove(view.QualifiedName);
        context.Resolved[view.QualifiedName] = found;
        return found;
    }

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

    private static TvfFenceOrigin? TryResolveNamedReference(NamedTableReference namedRef, ResolutionContext context)
    {
        var qualifiedName = context.Catalog.ResolveSynonymName(SchemaObjectNameHelper.Qualify(namedRef.SchemaObject));
        return context.ViewsByName.TryGetValue(qualifiedName, out var referencedView)
            ? Inherit(Resolve(referencedView, context))
            : null;
    }

    private static TvfFenceOrigin? Inherit(TvfFenceOrigin? origin) => origin is null ? null : origin with { Depth = origin.Depth + 1 };
}
