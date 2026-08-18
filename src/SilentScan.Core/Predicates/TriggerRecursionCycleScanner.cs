using Microsoft.SqlServer.TransactSql.ScriptDom;
using SilentScan.Core.Catalog;
using SilentScan.Core.Parsing;

namespace SilentScan.Core.Predicates;

/// <summary>
/// docs/detection-checklist.md "Second full-archive practitioner sweep (2026-08-18)" §G "Multi-hop
/// trigger recursion cycle across tables" - see <see cref="TriggerRecursionCycleFinding"/> for the
/// full precision story, the gating correction, and the oracle evidence. A WHOLE-SCAN pass, not a
/// per-file one: the two (or more) triggers forming a cycle routinely live in different files.
/// </summary>
public static class TriggerRecursionCycleScanner
{
    /// <summary>How deep the cycle search walks before giving up on a given starting table - see
    /// <see cref="TriggerRecursionCycleFinding"/>'s own doc comment for why this is a stated
    /// scope-down rather than an unbounded search.</summary>
    private const int MaxCycleDepth = 8;

    public static IReadOnlyList<TriggerRecursionCycleFinding> Scan(IEnumerable<SqlParseResult> parseResults, DatabaseCatalog catalog)
    {
        if (catalog.IsNestedTriggersEnabled != true)
        {
            // Live-mode-only, gated strictly on a live-confirmed TRUE - file-mode (null) and a
            // live-confirmed FALSE both mean a cross-table cascade is a structural no-op past the
            // first hop, so never overclaim a risk that is not actually live (see
            // DatabaseCatalog.IsNestedTriggersEnabled's own doc comment).
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
            FindCycles(startTable, startTable, byFromTable, [], visited, seenCanonicalKeys, findings, depth: 0);
        }

        return findings;
    }

    private static void FindCycles(
        string startTable, string currentTable, IReadOnlyDictionary<string, IReadOnlyList<TriggerRecursionCycleHop>> graph,
        List<TriggerRecursionCycleHop> path, HashSet<string> visited, HashSet<string> seenCanonicalKeys,
        List<TriggerRecursionCycleFinding> findings, int depth)
    {
        if (depth >= MaxCycleDepth || !graph.TryGetValue(currentTable, out var outEdges))
        {
            return;
        }

        foreach (var edge in outEdges)
        {
            if (path.Count == 0 && string.Equals(edge.ToTableQualifiedName, startTable, StringComparison.OrdinalIgnoreCase))
            {
                // A single-hop self-loop is DirectRecursiveTrigger's own claim, not this stream's.
                continue;
            }

            if (string.Equals(edge.ToTableQualifiedName, startTable, StringComparison.OrdinalIgnoreCase))
            {
                RecordCycle([.. path, edge], seenCanonicalKeys, findings);
                continue;
            }

            if (visited.Contains(edge.ToTableQualifiedName))
            {
                // Only simple cycles (no repeated intermediate table) count - a table revisited
                // mid-path would just be a shorter cycle this same starting-table search already
                // finds (or will find) on its own.
                continue;
            }

            visited.Add(edge.ToTableQualifiedName);
            path.Add(edge);
            FindCycles(startTable, edge.ToTableQualifiedName, graph, path, visited, seenCanonicalKeys, findings, depth + 1);
            path.RemoveAt(path.Count - 1);
            visited.Remove(edge.ToTableQualifiedName);
        }
    }

    /// <summary>
    /// Canonicalizes the closed cycle (a list of hops whose FromTable sequence revisits its own
    /// first table at the end) by rotating it to start at its alphabetically-first table - the same
    /// real cycle discovered from two different starting tables during the outer search collapses
    /// to the same canonical key here and is only reported once.
    /// </summary>
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
            // A DDL/LOGON trigger (TriggerObject.Name null) has no DML target table of its own to
            // start a cycle from - the same guard TypedPredicateExtractor/NonSargablePredicateScanner
            // already apply for the identical reason.
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

    /// <summary>
    /// Every DISTINCT real base table (other than the trigger's own target - a self-write is
    /// DirectRecursiveTrigger's own claim, not this pass's) that this trigger's own body writes to
    /// directly, first-occurrence line only. Only direct <see cref="NamedTableReference"/> targets
    /// count - never a view, never a temp table/table variable (private per session, cannot
    /// participate in a cross-table trigger cascade the same way).
    /// </summary>
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
            if (target is not NamedTableReference named)
            {
                return;
            }

            var qualifiedName = catalog.ResolveSynonymName(SchemaObjectNameHelper.Qualify(named.SchemaObject));
            if (string.Equals(qualifiedName, ownTargetQualifiedName, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            if (catalog.Find(qualifiedName) is not { Kind: CatalogTableKind.Table })
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
