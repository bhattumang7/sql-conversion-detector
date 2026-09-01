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

    private static readonly HashSet<string> FrameIncapableFunctions = new(StringComparer.OrdinalIgnoreCase)
    {
        "ROW_NUMBER",
        "RANK",
        "DENSE_RANK",
        "NTILE",
        "LAG",
        "LEAD",
        "PERCENT_RANK",
        "CUME_DIST",
    };

    internal sealed class Rule(string sourcePath) : IModuleRule
    {
        public List<WindowFrameFinding> Findings { get; } = [];

        public void OnEnterFunctionCall(FunctionCall node, ModuleWalker walker)
        {
            if (node.OverClause is not { OrderByClause: not null } overClause
                || FrameIncapableFunctions.Contains(node.FunctionName?.Value ?? string.Empty))
            {
                return;
            }

            if (overClause.WindowFrameClause is null)
            {
                Findings.Add(new WindowFrameFinding(
                    WindowFrameFindingKind.ImplicitDefaultRangeFrame, sourcePath, overClause.StartLine, overClause.StartColumn));
            }
            else if (overClause.WindowFrameClause.WindowFrameType == WindowFrameType.Range)
            {
                Findings.Add(new WindowFrameFinding(
                    WindowFrameFindingKind.ExplicitRangeFrame, sourcePath,
                    overClause.WindowFrameClause.StartLine, overClause.WindowFrameClause.StartColumn));
            }
        }
    }
}
