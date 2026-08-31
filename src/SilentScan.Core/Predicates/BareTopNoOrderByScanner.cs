using Microsoft.SqlServer.TransactSql.ScriptDom;
using SilentScan.Core.Catalog;
using SilentScan.Core.Lineage;
using SilentScan.Core.Parsing;

namespace SilentScan.Core.Predicates;

public static class BareTopNoOrderByScanner
{
    public static IReadOnlyList<BareTopNoOrderByFinding> Scan(SqlParseResult parseResult)
    {
        var rule = CreateRule(parseResult.SourcePath);
        var walker = new ModuleWalker(parseResult.SourcePath, new DatabaseCatalog(), EmptyResolvedViews, rules: [rule]);
        parseResult.Fragment.Accept(walker);
    return Harvest(rule);
    }
    internal static Rule CreateRule(string sourcePath) => new(sourcePath);

    internal static IReadOnlyList<BareTopNoOrderByFinding> Harvest(Rule rule) =>
            [
            .. rule.Findings
                .OrderBy(f => f.SourcePath, StringComparer.Ordinal)
                .ThenBy(f => f.Line)
                .ThenBy(f => f.Column),
        ];


    private static readonly IReadOnlyDictionary<string, ResolvedRelation> EmptyResolvedViews = new Dictionary<string, ResolvedRelation>();

    internal sealed class Rule(string sourcePath) : IModuleRule
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
            top.Percent && IsOneHundredLiteral(Unwrap(top.Expression));

        private static bool IsOneHundredLiteral(ScalarExpression expression) =>
            expression switch
            {
                IntegerLiteral { Value: "100" } => true,
                NumericLiteral { Value: var value } => IsExactlyOneHundred(value),
                _ => false,
            };

        private static bool IsExactlyOneHundred(string value)
        {
            var dot = value.IndexOf('.', StringComparison.Ordinal);
            var integerPart = dot < 0 ? value : value[..dot];
            var fractionalPart = dot < 0 ? string.Empty : value[(dot + 1)..];
            return integerPart == "100" && fractionalPart.All(c => c == '0');
        }

        private static ScalarExpression Unwrap(ScalarExpression expression) =>
            expression is ParenthesisExpression parenthesis ? Unwrap(parenthesis.Expression) : expression;
    }
}
