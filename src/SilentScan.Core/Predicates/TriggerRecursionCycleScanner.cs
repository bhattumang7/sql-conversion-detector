using Microsoft.SqlServer.TransactSql.ScriptDom;
using SilentScan.Core.Catalog;
using SilentScan.Core.Parsing;
using SilentScan.Core.Common;

namespace SilentScan.Core.Predicates;

public static class TriggerRecursionCycleScanner
{
    private const int MaxCycleDepth = 8;

    public static IReadOnlyList<TriggerRecursionCycleFinding> Scan(IEnumerable<SqlParseResult> parseResults, DatabaseCatalog catalog)
    {
        if (catalog.IsNestedTriggersEnabled != true)
        {

            return [];
        }

        var edges = new List<TriggerRecursionCycleHop>();
        foreach (var result in parseResults)
        {
            var visitor = new Visitor(result.SourcePath, catalog);
            result.Fragment.Accept(visitor);
            edges.AddRange(visitor.Edges);
        }

        var byFromTable = edges
            .GroupBy(e => e.FromTableQualifiedName, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(
                g => g.Key,
                g => (IReadOnlyList<TriggerRecursionCycleHop>)
                    [.. g.OrderBy(e => e.ToTableQualifiedName, StringComparer.Ordinal).ThenBy(e => e.SourcePath, StringComparer.Ordinal).ThenBy(e => e.TriggerLine)],
                StringComparer.OrdinalIgnoreCase);

        var seenCanonicalKeys = new HashSet<string>(StringComparer.Ordinal);
        var findings = new List<TriggerRecursionCycleFinding>();

        foreach (var startTable in byFromTable.Keys.OrderBy(k => k, StringComparer.Ordinal))
        {
            var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { startTable };
            var context = new CycleSearchContext(startTable, byFromTable, seenCanonicalKeys, findings);
            FindCycles(context, startTable, [], visited, depth: 0);
        }

        return findings;
    }

    private readonly record struct CycleSearchContext(
        string StartTable,
        IReadOnlyDictionary<string, IReadOnlyList<TriggerRecursionCycleHop>> Graph,
        HashSet<string> SeenCanonicalKeys,
        List<TriggerRecursionCycleFinding> Findings);

    private static void FindCycles(
        CycleSearchContext context, string currentTable, List<TriggerRecursionCycleHop> path, HashSet<string> visited, int depth)
    {
        if (depth >= MaxCycleDepth || !context.Graph.TryGetValue(currentTable, out var outEdges))
        {
            return;
        }

        foreach (var edge in outEdges)
        {
            if (path.Count == 0 && string.Equals(edge.ToTableQualifiedName, context.StartTable, StringComparison.OrdinalIgnoreCase))
            {

                continue;
            }

            if (string.Equals(edge.ToTableQualifiedName, context.StartTable, StringComparison.OrdinalIgnoreCase))
            {
                RecordCycle([.. path, edge], context.SeenCanonicalKeys, context.Findings);
                continue;
            }

            if (visited.Contains(edge.ToTableQualifiedName))
            {

                continue;
            }

            visited.Add(edge.ToTableQualifiedName);
            path.Add(edge);
            FindCycles(context, edge.ToTableQualifiedName, path, visited, depth + 1);
            path.RemoveAt(path.Count - 1);
            visited.Remove(edge.ToTableQualifiedName);
        }
    }

    private static void RecordCycle(IReadOnlyList<TriggerRecursionCycleHop> hops, HashSet<string> seenCanonicalKeys, List<TriggerRecursionCycleFinding> findings)
    {
        var tables = hops.Select(h => h.FromTableQualifiedName).ToList();
        var minIndex = 0;
        for (var i = 1; i < tables.Count; i++)
        {
            if (string.CompareOrdinal(tables[i], tables[minIndex]) < 0)
            {
                minIndex = i;
            }
        }

        var rotatedHops = new List<TriggerRecursionCycleHop>(hops.Count);
        for (var i = 0; i < hops.Count; i++)
        {
            rotatedHops.Add(hops[(minIndex + i) % hops.Count]);
        }

        var canonicalKey = string.Join("->", rotatedHops.Select(h => $"{h.FromTableQualifiedName}|{h.ToTableQualifiedName}|{h.TriggerQualifiedName}"));
        if (!seenCanonicalKeys.Add(canonicalKey))
        {
            return;
        }

        var cycleTables = rotatedHops.Select(h => h.FromTableQualifiedName).ToList();
        findings.Add(new TriggerRecursionCycleFinding(cycleTables, rotatedHops));
    }

    private sealed class Visitor(string sourcePath, DatabaseCatalog catalog) : TSqlFragmentVisitor
    {
        public List<TriggerRecursionCycleHop> Edges { get; } = [];

        public override void ExplicitVisit(CreateTriggerStatement node) => VisitTrigger(node, node.Name, node.TriggerObject, node.StatementList);

        public override void ExplicitVisit(AlterTriggerStatement node) => VisitTrigger(node, node.Name, node.TriggerObject, node.StatementList);

        public override void ExplicitVisit(CreateOrAlterTriggerStatement node) => VisitTrigger(node, node.Name, node.TriggerObject, node.StatementList);

        private void VisitTrigger(TriggerStatementBody node, SchemaObjectName name, TriggerObject triggerObject, StatementList? statementList)
        {

            if (triggerObject.Name is not { } targetTableName || statementList is null)
            {
                return;
            }

            var qualifiedTriggerName = SchemaObjectNameHelper.Qualify(name);
            var fromTable = catalog.ResolveSynonymName(SchemaObjectNameHelper.Qualify(targetTableName));
            if (catalog.Find(fromTable) is not { Kind: CatalogTableKind.Table })
            {
                return;
            }

            var targetCollector = new WriteTargetCollector(catalog, fromTable);
            statementList.Accept(targetCollector);

            foreach (var (toTable, line) in targetCollector.Writes)
            {
                Edges.Add(new TriggerRecursionCycleHop(qualifiedTriggerName, sourcePath, node.StartLine, fromTable, toTable, line));
            }
        }
    }

    private sealed class WriteTargetCollector(DatabaseCatalog catalog, string ownTargetQualifiedName) : TSqlFragmentVisitor
    {
        private readonly HashSet<string> _seen = new(StringComparer.OrdinalIgnoreCase);

        public List<(string ToTableQualifiedName, int Line)> Writes { get; } = [];

        public override void ExplicitVisit(InsertStatement node) => Record(node.InsertSpecification.Target, node.StartLine);

        public override void ExplicitVisit(UpdateStatement node) => Record(node.UpdateSpecification.Target, node.StartLine);

        public override void ExplicitVisit(DeleteStatement node) => Record(node.DeleteSpecification.Target, node.StartLine);

        public override void ExplicitVisit(MergeStatement node) => Record(node.MergeSpecification.Target, node.StartLine);

        private void Record(TableReference? target, int line)
        {
            if (DmlWriteTargetResolver.TryResolve(target, withCtes: null, catalog) is not { } qualifiedName
                || string.Equals(qualifiedName, ownTargetQualifiedName, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            if (_seen.Add(qualifiedName))
            {
                Writes.Add((qualifiedName, line));
            }
        }
    }
}
