using Microsoft.SqlServer.TransactSql.ScriptDom;
using SilentScan.Core.Parsing;

namespace SilentScan.Core.Predicates;

public static class BareTopNoOrderByScanner
{
    public static IReadOnlyList<BareTopNoOrderByFinding> Scan(SqlParseResult parseResult)
    {
        var visitor = new Visitor(parseResult.SourcePath);
        parseResult.Fragment.Accept(visitor);
        return
        [
            .. visitor.Findings
                .OrderBy(f => f.SourcePath, StringComparer.Ordinal)
                .ThenBy(f => f.Line)
                .ThenBy(f => f.Column),
        ];
    }

    private sealed class Visitor(string sourcePath) : TSqlFragmentVisitor
    {
        public List<BareTopNoOrderByFinding> Findings { get; } = [];

        public override void ExplicitVisit(QuerySpecification node)
        {
            if (node.TopRowFilter is { } top && node.OrderByClause is null && !IsHundredPercent(top))
            {
                Findings.Add(new BareTopNoOrderByFinding(sourcePath, node.TopRowFilter.StartLine, node.TopRowFilter.StartColumn));
            }

            base.ExplicitVisit(node);
        }

private static bool IsHundredPercent(TopRowFilter top) =>
            top.Percent && Unwrap(top.Expression) is IntegerLiteral { Value: "100" };

        private static ScalarExpression Unwrap(ScalarExpression expression) =>
            expression is ParenthesisExpression parenthesis ? Unwrap(parenthesis.Expression) : expression;
    }
}
