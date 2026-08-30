using Microsoft.SqlServer.TransactSql.ScriptDom;
using SilentScan.Core.Catalog;
using SilentScan.Core.Lineage;
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
            var rule = new Rule(result.SourcePath, catalog);
            var walker = new ModuleWalker(result.SourcePath, catalog, EmptyResolvedViews, ledger: null, currentProcScope: null, callerScopeByCalleeScope: null, rules: [rule]);
            result.Fragment.Accept(walker);
            edges.AddRange(rule.Edges);
        }

        var byFromTable = edges
            .GroupBy(e => e.FromTableQualifiedName, catalog.IdentifierComparer)
            .ToDictionary(
                g => g.Key,
                g => (IReadOnlyList<TriggerRecursionCycleHop>)
                    [.. g.OrderBy(e => e.ToTableQualifiedName, StringComparer.Ordinal).ThenBy(e => e.SourcePath, StringComparer.Ordinal).ThenBy(e => e.TriggerLine)],
                catalog.IdentifierComparer);

        var seenCanonicalKeys = new HashSet<string>(StringComparer.Ordinal);
        var findings = new List<TriggerRecursionCycleFinding>();

        foreach (var startTable in byFromTable.Keys.OrderBy(k => k, StringComparer.Ordinal))
        {
            var visited = new HashSet<string>(catalog.IdentifierComparer) { startTable };
            var context = new CycleSearchContext(startTable, byFromTable, seenCanonicalKeys, findings, catalog.IdentifierComparer);
            FindCycles(context, startTable, [], visited, depth: 0);
        }

        return findings;
    }

    private readonly record struct CycleSearchContext(
        string StartTable,
        IReadOnlyDictionary<string, IReadOnlyList<TriggerRecursionCycleHop>> Graph,
        HashSet<string> SeenCanonicalKeys,
        List<TriggerRecursionCycleFinding> Findings,
        StringComparer IdentifierComparer);

    private static void FindCycles(
        CycleSearchContext context, string currentTable, List<TriggerRecursionCycleHop> path, HashSet<string> visited, int depth)
    {
        if (depth >= MaxCycleDepth || !context.Graph.TryGetValue(currentTable, out var outEdges))
        {
            return;
        }

        foreach (var edge in outEdges)
        {
            if (path.Count == 0 && context.IdentifierComparer.Equals(edge.ToTableQualifiedName, context.StartTable))
            {

                continue;
            }

            if (context.IdentifierComparer.Equals(edge.ToTableQualifiedName, context.StartTable))
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

    private static readonly IReadOnlyDictionary<string, ResolvedRelation> EmptyResolvedViews = new Dictionary<string, ResolvedRelation>();

    private sealed class Rule(string sourcePath, DatabaseCatalog catalog) : IModuleRule
    {
        public List<TriggerRecursionCycleHop> Edges { get; } = [];

        public void OnEnterTriggerStatementScope(TriggerStatementBody node, SchemaObjectName name, TriggerObject triggerObject, ModuleWalker walker) =>
            VisitTrigger(node, name, triggerObject, node.StatementList);

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
        private readonly HashSet<string> _seen = new(catalog.IdentifierComparer);

        public List<(string ToTableQualifiedName, int Line)> Writes { get; } = [];

        public override void ExplicitVisit(InsertStatement node) => Record(node.InsertSpecification.Target, node.StartLine);

        public override void ExplicitVisit(UpdateStatement node) => Record(node.UpdateSpecification.Target, node.StartLine);

        public override void ExplicitVisit(DeleteStatement node) => Record(node.DeleteSpecification.Target, node.StartLine);

        public override void ExplicitVisit(MergeStatement node) => Record(node.MergeSpecification.Target, node.StartLine);

        private void Record(TableReference? target, int line)
        {
            if (DmlWriteTargetResolver.TryResolve(target, withCtes: null, catalog) is not { } qualifiedName
                || catalog.IdentifierComparer.Equals(qualifiedName, ownTargetQualifiedName))
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
