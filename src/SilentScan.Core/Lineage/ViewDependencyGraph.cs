using Microsoft.SqlServer.TransactSql.ScriptDom;
using SilentScan.Core.Catalog;

namespace SilentScan.Core.Lineage;

/// <summary>
/// Dependency graph over view definitions: view -&gt; the views it (transitively, through
/// subqueries too) references. Base tables are leaves, not graph nodes. CLAUDE.md: "Views
/// in topological order of dependency; cycles -&gt; Unknown."
/// </summary>
public static class ViewDependencyGraph
{
    /// <returns>Views in dependency order (a view's dependencies appear before it), and the set of qualified names involved in a dependency cycle.</returns>
    public static (IReadOnlyList<ViewDefinition> Order, IReadOnlySet<string> CyclicViews) TopologicalSort(
        IReadOnlyList<ViewDefinition> views, DatabaseCatalog catalog)
    {
        // A corpus scan can see the same view name defined more than once - e.g. incremental
        // upgrade scripts that each re-issue CREATE VIEW for the same object across a
        // project's version history. Last one wins, consistent with CatalogBuilder's
        // AddOrReplace semantics for tables (real deployments apply scripts in order, so the
        // last CREATE is the one that's actually live).
        var byName = new Dictionary<string, ViewDefinition>(StringComparer.OrdinalIgnoreCase);
        foreach (var view in views)
        {
            byName[view.QualifiedName] = view;
        }

        var dedupedViews = byName.Values.ToList();
        var edges = dedupedViews.ToDictionary(
            v => v.QualifiedName,
            v => FindReferencedViewNames(v.SelectStatement, byName.Keys, catalog),
            StringComparer.OrdinalIgnoreCase);

        var state = new TraversalState(byName, edges);

        foreach (var view in dedupedViews)
        {
            VisitNode(view.QualifiedName, state);
        }

        return (state.Order, state.Cyclic);
    }

    private static void VisitNode(string name, TraversalState state)
    {
        if (state.Visited.Contains(name))
        {
            return;
        }

        if (state.PathSet.Contains(name))
        {
            // Back-edge to a node already on the current DFS path: every node from its
            // first occurrence to here (inclusive) is part of this cycle.
            var cycleStart = state.Path.IndexOf(name);
            for (var i = cycleStart; i < state.Path.Count; i++)
            {
                state.Cyclic.Add(state.Path[i]);
            }

            return;
        }

        state.Path.Add(name);
        state.PathSet.Add(name);

        foreach (var dependency in state.Edges[name])
        {
            VisitNode(dependency, state);
        }

        state.Path.RemoveAt(state.Path.Count - 1);
        state.PathSet.Remove(name);

        state.Visited.Add(name);
        state.Order.Add(state.ByName[name]);
    }

    private static HashSet<string> FindReferencedViewNames(SelectStatement selectStatement, IEnumerable<string> knownViewNames, DatabaseCatalog catalog)
    {
        var collector = new TableReferenceCollector(CteNameCollector.Collect(selectStatement));
        selectStatement.Accept(collector);

        // A view defined over a synonym for another view must still get a dependency edge to
        // that view - collector.QualifiedNames names the synonym itself, which never matches
        // knownViewNames on its own, so topological order could resolve the outer view before
        // the inner one and its columns would silently degrade to Unknown (the same failure
        // mode the TVF-to-TVF edge fix below already guards against).
        var known = new HashSet<string>(knownViewNames, StringComparer.OrdinalIgnoreCase);
        return [.. collector.QualifiedNames.Select(catalog.ResolveSynonymName).Where(known.Contains)];
    }

    private sealed class TableReferenceCollector(IReadOnlySet<string> cteNames) : TSqlFragmentVisitor
    {
        public List<string> QualifiedNames { get; } = [];

        public override void Visit(NamedTableReference node)
        {
            // CTE names shadow catalog views of the same name (FromScopeResolver's own rule) - a
            // CTE is never schema-qualified, so an unqualified reference matching an in-scope CTE
            // can never mean a real view instead. Without this, `CREATE VIEW dbo.Foo AS WITH Foo
            // AS (...) SELECT * FROM Foo` created a self-edge (a false cycle, poisoning dbo.Foo to
            // Unknown), and a CTE coinciding with an unrelated real view elsewhere created a false
            // dependency edge to it.
            if (node.SchemaObject.SchemaIdentifier is null && cteNames.Contains(node.SchemaObject.BaseIdentifier.Value))
            {
                return;
            }

            QualifiedNames.Add(SchemaObjectNameHelper.Qualify(node.SchemaObject));
        }

        // An inline TVF calling another inline TVF in its own FROM clause (FROM
        // dbo.other_itvf(...)) is a SchemaObjectFunctionTableReference, not a
        // NamedTableReference - missing this meant no dependency edge was recorded between
        // them, so topological order could resolve the outer TVF before the inner one and its
        // columns would degrade to Unknown, and a genuine TVF-to-TVF cycle went undetected
        // entirely (coverage-remediation-plan.md Phase 3.5).
        public override void Visit(SchemaObjectFunctionTableReference node) =>
            QualifiedNames.Add(SchemaObjectNameHelper.Qualify(node.SchemaObject));
    }

    private sealed class TraversalState(Dictionary<string, ViewDefinition> byName, Dictionary<string, HashSet<string>> edges)
    {
        public Dictionary<string, ViewDefinition> ByName { get; } = byName;

        public Dictionary<string, HashSet<string>> Edges { get; } = edges;

        public List<ViewDefinition> Order { get; } = [];

        public HashSet<string> Visited { get; } = new(StringComparer.OrdinalIgnoreCase);

        public List<string> Path { get; } = [];

        public HashSet<string> PathSet { get; } = new(StringComparer.OrdinalIgnoreCase);

        public HashSet<string> Cyclic { get; } = new(StringComparer.OrdinalIgnoreCase);
    }
}
