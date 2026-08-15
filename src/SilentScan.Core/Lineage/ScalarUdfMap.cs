using Microsoft.SqlServer.TransactSql.ScriptDom;
using SilentScan.Core.Catalog;

namespace SilentScan.Core.Lineage;

/// <summary>
/// Pass 2 sibling to <see cref="TvfFenceMap"/>: for every view/inline TVF, whether its own
/// definition calls a scalar UDF - directly, or inherited through however many further view/iTVF
/// layers. A caller referencing the view sees an ordinary table reference; only this map, built
/// once against the resolved catalog, says the view's expansion actually drags per-row scalar-UDF
/// execution (and, pre-2019, a forced-serial plan) into whatever query names it.
/// </summary>
public static class ScalarUdfMap
{
    /// <param name="views">Every CREATE VIEW and inline-TVF definition seen (<see cref="ViewDefinitionExtractor.Extract"/>'s <c>Views</c> - a multi-statement/CLR TVF has no body here, so it is never a carrier, matching the MSTVF-as-fence stream's own opacity boundary).</param>
    /// <param name="catalog">Resolved catalog carrying every scalar UDF's <see cref="ScalarUdfInfo"/>.</param>
    public static IReadOnlyDictionary<string, ScalarUdfOrigin> Build(IReadOnlyList<ViewDefinition> views, DatabaseCatalog catalog)
    {
        var viewsByName = new Dictionary<string, ViewDefinition>(StringComparer.OrdinalIgnoreCase);
        foreach (var view in views)
        {
            viewsByName[view.QualifiedName] = view;
        }

        var context = new ResolutionContext(
            viewsByName, catalog, new Dictionary<string, ScalarUdfOrigin?>(StringComparer.OrdinalIgnoreCase), new HashSet<string>(StringComparer.OrdinalIgnoreCase));

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
        Dictionary<string, ScalarUdfOrigin?> Resolved,
        HashSet<string> InProgress);

    private static ScalarUdfOrigin? Resolve(ViewDefinition view, ResolutionContext context)
    {
        if (context.Resolved.TryGetValue(view.QualifiedName, out var cached))
        {
            return cached;
        }

        // Cyclic view dependency: ViewDependencyGraph already reports the cycle itself as a
        // lineage problem, so this map just resolves to no origin rather than looping.
        if (!context.InProgress.Add(view.QualifiedName))
        {
            return null;
        }

        var direct = FindWorstDirectCall(view, context.Catalog);

        var (_, namedRefs) = TvfReferenceWalker.CollectFromClauses(view.SelectStatement);
        var inherited = namedRefs
            .Select(named => TryResolveNamedReference(named, context))
            .Where(origin => origin is not null)
            .Select(origin => Inherit(origin!))
            .FirstOrDefault();

        var found = Worse(direct, inherited);

        context.InProgress.Remove(view.QualifiedName);
        context.Resolved[view.QualifiedName] = found;
        return found;
    }

    /// <summary>A plain table-name reference only ever inherits an origin (it can never introduce one) - null when the name isn't a known view/iTVF, or that view/iTVF carries no origin of its own.</summary>
    private static ScalarUdfOrigin? TryResolveNamedReference(NamedTableReference namedRef, ResolutionContext context)
    {
        var qualifiedName = context.Catalog.ResolveSynonymName(SchemaObjectNameHelper.Qualify(namedRef.SchemaObject));
        return context.ViewsByName.TryGetValue(qualifiedName, out var referencedView)
            ? Resolve(referencedView, context)
            : null;
    }

    private static ScalarUdfOrigin? Inherit(ScalarUdfOrigin origin) => origin with { Depth = origin.Depth + 1 };

    /// <summary>Predicate context is worse than projection regardless of depth - a predicate-context origin always wins when both exist, otherwise the direct (lower-depth) candidate wins, matching <see cref="TvfFenceMap"/>'s own "direct beats inherited" simplification.</summary>
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

    /// <summary>
    /// Scans this view/iTVF's OWN select statement (never recursing into a nested view/iTVF's
    /// text - that happens through <see cref="Resolve"/>'s own recursion instead) for the worst
    /// (predicate-context beats projection-context; first-found otherwise) direct scalar-UDF
    /// call. A 2-part function call that doesn't resolve in the catalog's scalar-UDF registry is
    /// never a candidate - same "never guess" rule as every other catalog-gated stream. Region
    /// classification (which exact clause a call sits in) mirrors
    /// <see cref="Predicates.ScalarUdfScanner"/>'s own - the two are independent AST walks over
    /// different-shaped input (a whole module vs a single view body) rather than a shared
    /// component, matching how <see cref="TvfFenceMap"/> and <c>TvfFenceScanner</c> already stay
    /// separate despite overlapping FROM-clause logic.
    /// </summary>
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

        private void ClaimRegion(TSqlFragment? region, TSqlFragment node, ScalarUdfContext context)
        {
            if (region is not null)
            {
                _regions.Add((region.StartOffset, region.StartOffset + region.FragmentLength, context));
            }

            node.AcceptChildren(this);
        }

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

        private ScalarUdfContext ResolveContext(FunctionCall node)
        {
            var best = default((int Start, int End, ScalarUdfContext Context)?);
            foreach (var region in _regions)
            {
                if (node.StartOffset < region.Start || node.StartOffset >= region.End)
                {
                    continue;
                }

                if (best is null || region.End - region.Start < best.Value.End - best.Value.Start)
                {
                    best = region;
                }
            }

            return best?.Context ?? ScalarUdfContext.Other;
        }

        private static ScalarUdfOrigin? WorseOf(ScalarUdfOrigin? existing, ScalarUdfOrigin candidate)
        {
            if (existing is null)
            {
                return candidate;
            }

            return existing.OriginContext.IsPredicate() ? existing : candidate.OriginContext.IsPredicate() ? candidate : existing;
        }
    }
}
