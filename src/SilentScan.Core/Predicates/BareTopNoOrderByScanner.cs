using Microsoft.SqlServer.TransactSql.ScriptDom;
using SilentScan.Core.Catalog;
using SilentScan.Core.Lineage;
using SilentScan.Core.Parsing;

namespace SilentScan.Core.Predicates;

public static class BareTopNoOrderByScanner
{
    public static IReadOnlyList<BareTopNoOrderByFinding> Scan(SqlParseResult parseResult)
    {
        var rule = new Rule(parseResult.SourcePath);
        var walker = new ModuleWalker(parseResult.SourcePath, new DatabaseCatalog(), EmptyResolvedViews, ledger: null, currentProcScope: null, callerScopeByCalleeScope: null, rules: [rule]);
        parseResult.Fragment.Accept(walker);
        return
        [
            .. rule.Findings
                .OrderBy(f => f.SourcePath, StringComparer.Ordinal)
                .ThenBy(f => f.Line)
                .ThenBy(f => f.Column),
        ];
    }

    private static readonly IReadOnlyDictionary<string, ResolvedRelation> EmptyResolvedViews = new Dictionary<string, ResolvedRelation>();

    private sealed class Rule(string sourcePath) : IModuleRule
    {
        public List<BareTopNoOrderByFinding> Findings { get; } = [];

        public void OnEnterQuerySpecificationScope(QuerySpecification node, ScopeChain scopeChain, ModuleWalker walker)
        {
            if (node.TopRowFilter is { } top && node.OrderByClause is null && !IsHundredPercent(top))
            {
                Findings.Add(new BareTopNoOrderByFinding(sourcePath, top.StartLine, top.StartColumn));
            }
        }

        private static bool IsHundredPercent(TopRowFilter top) =>
            top.Percent && Unwrap(top.Expression) is IntegerLiteral { Value: "100" };

        private static ScalarExpression Unwrap(ScalarExpression expression) =>
            expression is ParenthesisExpression parenthesis ? Unwrap(parenthesis.Expression) : expression;
    }
}
