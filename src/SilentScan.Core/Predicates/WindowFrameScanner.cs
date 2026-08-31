using Microsoft.SqlServer.TransactSql.ScriptDom;
using SilentScan.Core.Catalog;
using SilentScan.Core.Lineage;
using SilentScan.Core.Parsing;

namespace SilentScan.Core.Predicates;

public static class WindowFrameScanner
{
    public static IReadOnlyList<WindowFrameFinding> Scan(SqlParseResult parseResult)
    {
        var rule = CreateRule(parseResult.SourcePath);
        var walker = new ModuleWalker(parseResult.SourcePath, new DatabaseCatalog(), EmptyResolvedViews, rules: [rule]);
        parseResult.Fragment.Accept(walker);
    return Harvest(rule);
    }
    internal static Rule CreateRule(string sourcePath) => new(sourcePath);

    internal static IReadOnlyList<WindowFrameFinding> Harvest(Rule rule) =>
            [
            .. rule.Findings
                .OrderBy(f => f.Kind)
                .ThenBy(f => f.SourcePath, StringComparer.Ordinal)
                .ThenBy(f => f.Line)
                .ThenBy(f => f.Column),
        ];


    private static readonly IReadOnlyDictionary<string, ResolvedRelation> EmptyResolvedViews = new Dictionary<string, ResolvedRelation>();

    internal sealed class Rule(string sourcePath) : IModuleRule
    {
        public List<WindowFrameFinding> Findings { get; } = [];

        public void OnEnterOverClause(OverClause node, ModuleWalker walker)
        {
            if (node.OrderByClause is not null)
            {
                if (node.WindowFrameClause is null)
                {
                    Findings.Add(new WindowFrameFinding(
                        WindowFrameFindingKind.ImplicitDefaultRangeFrame, sourcePath, node.StartLine, node.StartColumn));
                }
                else if (node.WindowFrameClause.WindowFrameType == WindowFrameType.Range)
                {
                    Findings.Add(new WindowFrameFinding(
                        WindowFrameFindingKind.ExplicitRangeFrame, sourcePath,
                        node.WindowFrameClause.StartLine, node.WindowFrameClause.StartColumn));
                }
            }
        }
    }
}
