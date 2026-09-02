using Microsoft.SqlServer.TransactSql.ScriptDom;
using SilentScan.Core.Catalog;
using SilentScan.Core.Common;
using SilentScan.Core.Lineage;
using SilentScan.Core.Parsing;

namespace SilentScan.Core.Predicates;

public static class MemoryOptimizedLedgerConflictScanner
{
    public static IReadOnlyList<MemoryOptimizedLedgerConflictFinding> Scan(SqlParseResult parseResult)
    {
        var rule = CreateRule(parseResult.SourcePath);
        var walker = new ModuleWalker(parseResult.SourcePath, new DatabaseCatalog(), EmptyResolvedViews, rules: [rule]);
        parseResult.Fragment.Accept(walker);
        return Harvest(rule);
    }

    internal static Rule CreateRule(string sourcePath) => new(sourcePath);

    internal static IReadOnlyList<MemoryOptimizedLedgerConflictFinding> Harvest(Rule rule) =>
        [
            .. rule.Findings
                .OrderBy(f => f.SourcePath, StringComparer.Ordinal)
                .ThenBy(f => f.Line)
                .ThenBy(f => f.Column),
        ];

    private static readonly IReadOnlyDictionary<string, ResolvedRelation> EmptyResolvedViews = new Dictionary<string, ResolvedRelation>();

    internal sealed class Rule(string sourcePath) : IModuleRule
    {
        public List<MemoryOptimizedLedgerConflictFinding> Findings { get; } = [];

        public void OnEnterCreateTableStatement(CreateTableStatement node, ModuleWalker walker)
        {
            var isMemoryOptimized = node.Options.OfType<MemoryOptimizedTableOption>().Any(o => o.OptionState == OptionState.On);
            var isLedger = node.Options.OfType<LedgerTableOption>().Any(o => o.OptionState == OptionState.On);

            if (isMemoryOptimized && isLedger)
            {
                Findings.Add(new MemoryOptimizedLedgerConflictFinding(
                    SchemaObjectNameHelper.Qualify(node.SchemaObjectName), sourcePath, node.StartLine, node.StartColumn));
            }
        }
    }
}
