using Microsoft.SqlServer.TransactSql.ScriptDom;
using SilentScan.Core.Parsing;

namespace SilentScan.Core.Predicates;

/// <summary>
/// docs/detection-checklist.md "Second full-archive practitioner sweep" §G: "Bare TOP (n) with no
/// ORDER BY anywhere in the query" - see <see cref="BareTopNoOrderByFinding"/> for the full scope/
/// precision story. Pure AST, no catalog needed - visits every <see cref="QuerySpecification"/> in
/// the fragment, view/proc/ad-hoc alike.
/// </summary>
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

        /// <summary>
        /// <c>TOP (100) PERCENT</c> with no ORDER BY returns every row regardless of TOP's own
        /// row-selection nondeterminism - see <see cref="BareTopNoOrderByFinding"/>'s own doc
        /// comment for why this is deliberately excluded rather than a false negative.
        /// </summary>
        private static bool IsHundredPercent(TopRowFilter top) =>
            top.Percent && Unwrap(top.Expression) is IntegerLiteral { Value: "100" };

        private static ScalarExpression Unwrap(ScalarExpression expression) =>
            expression is ParenthesisExpression parenthesis ? Unwrap(parenthesis.Expression) : expression;
    }
}
