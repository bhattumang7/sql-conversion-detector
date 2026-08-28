using Microsoft.SqlServer.TransactSql.ScriptDom;
using SilentScan.Core.Catalog;
using SilentScan.Core.Common;

namespace SilentScan.Core.Lineage;

public static class ScalarUdfMap
{
    public static IReadOnlyDictionary<string, ScalarUdfOrigin> Build(IReadOnlyList<ViewDefinition> views, DatabaseCatalog catalog)
    {
        var viewsByName = new Dictionary<string, ViewDefinition>(catalog.IdentifierComparer);
        foreach (var view in views)
        {
            viewsByName[view.QualifiedName] = view;
        }

        var context = new ResolutionContext(
            viewsByName, catalog, new Dictionary<string, ScalarUdfOrigin?>(catalog.IdentifierComparer), new HashSet<string>(catalog.IdentifierComparer));

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
        Dictionary<string, ScalarUdfOrigin?> Resolved,
        HashSet<string> InProgress);

    private static ScalarUdfOrigin? Resolve(ViewDefinition view, ResolutionContext context)
    {
        if (context.Resolved.TryGetValue(view.QualifiedName, out var cached))
        {
            return cached;
        }

        if (!context.InProgress.Add(view.QualifiedName))
        {
            return null;
        }

        var direct = FindWorstDirectCall(view, context.Catalog);

        var (functionRefs, namedRefs) = TvfReferenceWalker.CollectFromClauses(view.SelectStatement, context.Catalog.IdentifierComparer);
        ScalarUdfOrigin? inherited = null;
        foreach (var origin in namedRefs.Select(named => TryResolveNamedReference(named, context))
            .Concat(functionRefs.Select(function => TryResolveFunctionReference(function, context)))
            .Where(origin => origin is not null)
            .Select(origin => Inherit(origin!)))
        {
            inherited = Worse(inherited, origin);
        }

        var found = Worse(direct, inherited);

        context.InProgress.Remove(view.QualifiedName);
        context.Resolved[view.QualifiedName] = found;
        return found;
    }

    private static ScalarUdfOrigin? TryResolveNamedReference(NamedTableReference namedRef, ResolutionContext context)
    {
        var qualifiedName = context.Catalog.ResolveSynonymName(SchemaObjectNameHelper.Qualify(namedRef.SchemaObject));
        return context.ViewsByName.TryGetValue(qualifiedName, out var referencedView)
            ? Resolve(referencedView, context)
            : null;
    }

    private static ScalarUdfOrigin? TryResolveFunctionReference(TvfLeafReference functionRef, ResolutionContext context)
    {
        var qualifiedName = context.Catalog.ResolveSynonymName(SchemaObjectNameHelper.Qualify(functionRef.Reference.SchemaObject));
        return context.ViewsByName.TryGetValue(qualifiedName, out var referencedView)
            ? Resolve(referencedView, context)
            : null;
    }

    private static ScalarUdfOrigin? Inherit(ScalarUdfOrigin origin) => origin with { Depth = origin.Depth + 1 };

    private static ScalarUdfOrigin? Worse(ScalarUdfOrigin? direct, ScalarUdfOrigin? inherited)
    {
        if (direct is null)
        {
            return inherited;
        }

        if (inherited is null)
        {
            return direct;
        }

        return inherited.OriginContext.IsPredicate() && !direct.OriginContext.IsPredicate()
            ? inherited
            : direct;
    }

    private static ScalarUdfOrigin? FindWorstDirectCall(ViewDefinition view, DatabaseCatalog catalog)
    {
        var visitor = new DirectCallVisitor(view.SourcePath, catalog);
        view.SelectStatement.Accept(visitor);
        return visitor.Best;
    }

    private sealed class DirectCallVisitor(string sourcePath, DatabaseCatalog catalog) : TSqlFragmentVisitor
    {
        private readonly List<(int Start, int End, ScalarUdfContext Context)> _regions = [];

        public ScalarUdfOrigin? Best { get; private set; }

        public override void ExplicitVisit(WhereClause node) => ClaimRegion(node.SearchCondition, node, ScalarUdfContext.Where);

        public override void ExplicitVisit(HavingClause node) => ClaimRegion(node.SearchCondition, node, ScalarUdfContext.Having);

        public override void ExplicitVisit(QualifiedJoin node) => ClaimRegion(node.SearchCondition, node, ScalarUdfContext.JoinOn);

        public override void ExplicitVisit(MergeSpecification node) => ClaimRegion(node.SearchCondition, node, ScalarUdfContext.MergeOn);

        public override void ExplicitVisit(SelectScalarExpression node) => ClaimRegion(node.Expression, node, ScalarUdfContext.SelectList);

        public override void ExplicitVisit(OrderByClause node) => ClaimRegion(node, node, ScalarUdfContext.OrderBy);

        public override void ExplicitVisit(GroupByClause node) => ClaimRegion(node, node, ScalarUdfContext.GroupBy);

        public override void ExplicitVisit(FunctionCall node)
        {
            if (node.CallTarget is MultiPartIdentifierCallTarget)
            {
                var qualifiedName = catalog.ResolveSynonymName(SchemaObjectNameHelper.QualifyFunctionCall(node));
                if (catalog.TryGetScalarUdfInfo(qualifiedName, out var info))
                {
                    var candidate = new ScalarUdfOrigin(
                        qualifiedName,
                        info!.Kind,
                        ResolveContext(node),
                        sourcePath,
                        node.StartLine,
                        Depth: 1);

                    Best = WorseOf(Best, candidate);
                }
            }

            base.ExplicitVisit(node);
        }

        private void ClaimRegion(TSqlFragment? region, TSqlFragment node, ScalarUdfContext context)
        {
            if (region is not null)
            {
                _regions.Add((region.StartOffset, region.StartOffset + region.FragmentLength, context));
            }

            node.AcceptChildren(this);
        }

        private ScalarUdfContext ResolveContext(FunctionCall node) =>
            ScalarUdfContextRegions.Resolve(_regions, node);

        private static ScalarUdfOrigin? WorseOf(ScalarUdfOrigin? existing, ScalarUdfOrigin candidate)
        {
            if (existing is null || (!existing.OriginContext.IsPredicate() && candidate.OriginContext.IsPredicate()))
            {
                return candidate;
            }

            return existing;
        }
    }
}
