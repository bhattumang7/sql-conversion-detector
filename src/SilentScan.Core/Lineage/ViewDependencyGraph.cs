using Microsoft.SqlServer.TransactSql.ScriptDom;
using SilentScan.Core.Catalog;
using SilentScan.Core.Common;

namespace SilentScan.Core.Lineage;

public static class ViewDependencyGraph
{
    public static (IReadOnlyList<ViewDefinition> Order, IReadOnlySet<string> CyclicViews) TopologicalSort(
        IReadOnlyList<ViewDefinition> views, DatabaseCatalog catalog)
    {

        var byName = new Dictionary<string, ViewDefinition>(catalog.IdentifierComparer);
        foreach (var view in views)
        {
            byName[view.QualifiedName] = view;
        }

        var dedupedViews = byName.Values.ToList();
        var edges = dedupedViews.ToDictionary(
            v => v.QualifiedName,
            v => FindReferencedViewNames(v.SelectStatement, byName.Keys, catalog),
            catalog.IdentifierComparer);

        var state = new TraversalState(byName, edges, catalog.IdentifierComparer);

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
        var collector = new TableReferenceCollector(CteNameCollector.Collect(selectStatement, catalog.IdentifierComparer));
        selectStatement.Accept(collector);

        var known = new HashSet<string>(knownViewNames, catalog.IdentifierComparer);
        return [.. collector.QualifiedNames.Select(catalog.ResolveSynonymName).Where(known.Contains)];
    }

    private sealed class TableReferenceCollector(IReadOnlySet<string> cteNames) : TSqlFragmentVisitor
    {
        public List<string> QualifiedNames { get; } = [];

        public override void Visit(NamedTableReference node)
        {

            if (node.SchemaObject.SchemaIdentifier is null && cteNames.Contains(node.SchemaObject.BaseIdentifier.Value))
            {
                return;
            }

            QualifiedNames.Add(SchemaObjectNameHelper.Qualify(node.SchemaObject));
        }

        public override void Visit(SchemaObjectFunctionTableReference node) =>
            QualifiedNames.Add(SchemaObjectNameHelper.Qualify(node.SchemaObject));
    }

    private sealed class TraversalState(Dictionary<string, ViewDefinition> byName, Dictionary<string, HashSet<string>> edges, StringComparer identifierComparer)
    {
        public Dictionary<string, ViewDefinition> ByName { get; } = byName;

        public Dictionary<string, HashSet<string>> Edges { get; } = edges;

        public List<ViewDefinition> Order { get; } = [];

        public HashSet<string> Visited { get; } = new(identifierComparer);

        public List<string> Path { get; } = [];

        public HashSet<string> PathSet { get; } = new(identifierComparer);

        public HashSet<string> Cyclic { get; } = new(identifierComparer);
    }
}
