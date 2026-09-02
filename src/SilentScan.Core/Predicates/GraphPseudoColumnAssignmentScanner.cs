using Microsoft.SqlServer.TransactSql.ScriptDom;
using SilentScan.Core.Catalog;
using SilentScan.Core.Lineage;
using SilentScan.Core.Parsing;

namespace SilentScan.Core.Predicates;

public static class GraphPseudoColumnAssignmentScanner
{
    public static IReadOnlyList<GraphPseudoColumnAssignmentFinding> Scan(SqlParseResult parseResult)
    {
        var rule = CreateRule(parseResult.SourcePath);
        var walker = new ModuleWalker(parseResult.SourcePath, new DatabaseCatalog(), EmptyResolvedViews, rules: [rule]);
        parseResult.Fragment.Accept(walker);
        return Harvest(rule);
    }

    internal static Rule CreateRule(string sourcePath) => new(sourcePath);

    internal static IReadOnlyList<GraphPseudoColumnAssignmentFinding> Harvest(Rule rule) =>
        [
            .. rule.Findings
                .OrderBy(f => f.SourcePath, StringComparer.Ordinal)
                .ThenBy(f => f.Line)
                .ThenBy(f => f.Column),
        ];

    private static readonly IReadOnlyDictionary<string, ResolvedRelation> EmptyResolvedViews = new Dictionary<string, ResolvedRelation>();

    private static string? PseudoColumnNameFor(ColumnType columnType) => columnType switch
    {
        ColumnType.PseudoColumnGraphNodeId => "$node_id",
        ColumnType.PseudoColumnGraphEdgeId => "$edge_id",
        _ => null,
    };

    internal sealed class Rule(string sourcePath) : IModuleRule
    {
        public List<GraphPseudoColumnAssignmentFinding> Findings { get; } = [];

        public void OnEnterInsertStatementScope(InsertStatement node, ModuleWalker walker)
        {
            foreach (var column in node.InsertSpecification.Columns)
            {
                if (PseudoColumnNameFor(column.ColumnType) is { } columnName)
                {
                    Findings.Add(new GraphPseudoColumnAssignmentFinding(columnName, "INSERT", sourcePath, column.StartLine, column.StartColumn));
                }
            }
        }

        public void OnEnterAssignmentSetClause(AssignmentSetClause node, ModuleWalker walker)
        {
            if (node.Column is { } column && PseudoColumnNameFor(column.ColumnType) is { } columnName)
            {
                Findings.Add(new GraphPseudoColumnAssignmentFinding(columnName, "UPDATE", sourcePath, node.StartLine, node.StartColumn));
            }
        }
    }
}
