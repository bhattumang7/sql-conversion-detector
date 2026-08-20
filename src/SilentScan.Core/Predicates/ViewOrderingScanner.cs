using Microsoft.SqlServer.TransactSql.ScriptDom;
using SilentScan.Core.Catalog;
using SilentScan.Core.Parsing;
using SilentScan.Core.Common;

namespace SilentScan.Core.Predicates;

/// <summary>
/// docs/detection-checklist.md "Small precise adds": "TOP(100) PERCENT ignored by the optimizer"
/// and "ORDER BY in a view / inline TVF" (see <see cref="ViewOrderingFinding"/> for the full
/// mechanism/scope writeup). Fully syntax-only - only a view's or an inline TVF's own OUTERMOST
/// query specification is inspected, the same "outermost query only, no inner-derived-table
/// guessing" discipline <see cref="SelectStarViewScanner.FindOutermostStarLine"/> already
/// established for the identical class of view-body-shape rule.
/// </summary>
public static class ViewOrderingScanner
{
    public static IReadOnlyList<ViewOrderingFinding> Scan(SqlParseResult parseResult)
    {
        var visitor = new Visitor(parseResult.SourcePath);
        parseResult.Fragment.Accept(visitor);
        return
        [
            .. visitor.Findings
                .OrderBy(f => f.Kind)
                .ThenBy(f => f.SourcePath, StringComparer.Ordinal)
                .ThenBy(f => f.Line)
                .ThenBy(f => f.Column),
        ];
    }

    private sealed class Visitor(string sourcePath) : TSqlFragmentVisitor
    {
        public List<ViewOrderingFinding> Findings { get; } = [];

        public override void ExplicitVisit(CreateViewStatement node) => Inspect(SchemaObjectNameHelper.Qualify(node.SchemaObjectName), node.SelectStatement.QueryExpression);

        public override void ExplicitVisit(AlterViewStatement node) => Inspect(SchemaObjectNameHelper.Qualify(node.SchemaObjectName), node.SelectStatement.QueryExpression);

        public override void ExplicitVisit(CreateOrAlterViewStatement node) => Inspect(SchemaObjectNameHelper.Qualify(node.SchemaObjectName), node.SelectStatement.QueryExpression);

        public override void ExplicitVisit(CreateFunctionStatement node) => InspectFunction(node.Name, node.ReturnType);

        public override void ExplicitVisit(AlterFunctionStatement node) => InspectFunction(node.Name, node.ReturnType);

        public override void ExplicitVisit(CreateOrAlterFunctionStatement node) => InspectFunction(node.Name, node.ReturnType);

        private void InspectFunction(SchemaObjectName name, FunctionReturnType returnType)
        {
            // Only an inline TVF (RETURNS TABLE AS RETURN (<select>)) has a single outermost query
            // to inspect the same way a view does - a multi-statement TVF's RETURNS @t TABLE(...)
            // body has no such single query, and a scalar function never returns a result set at
            // all, so neither is a candidate.
            if (returnType is SelectFunctionReturnType selectReturn)
            {
                Inspect(SchemaObjectNameHelper.Qualify(name), selectReturn.SelectStatement.QueryExpression);
            }
        }

        private void Inspect(string qualifiedName, QueryExpression queryExpression)
        {
            var spec = OutermostQuerySpecification(queryExpression);
            if (spec is null || spec.OrderByClause is null)
            {
                return;
            }

            if (spec.TopRowFilter is { } top)
            {
                var isHundredPercent = top.Percent && IsLiteralValue(top.Expression, "100");
                if (isHundredPercent)
                {
                    Findings.Add(new ViewOrderingFinding(
                        ViewOrderingFindingKind.TopPercentOrderByNeverLimits, qualifiedName, sourcePath,
                        spec.StartLine, spec.StartColumn, FindingConfidence.High));
                }
                else
                {
                    Findings.Add(new ViewOrderingFinding(
                        ViewOrderingFindingKind.OrderByNotGuaranteedToConsumer, qualifiedName, sourcePath,
                        spec.StartLine, spec.StartColumn, FindingConfidence.Low));
                }
            }
            else if (spec.OffsetClause is not null)
            {
                Findings.Add(new ViewOrderingFinding(
                    ViewOrderingFindingKind.OrderByNotGuaranteedToConsumer, qualifiedName, sourcePath,
                    spec.StartLine, spec.StartColumn, FindingConfidence.Low));
            }

            // No TOP and no OFFSET with an ORDER BY present would be Msg 1033 - unreachable in
            // valid, already-deployed SQL, so nothing else to handle here.
        }

        private static QuerySpecification? OutermostQuerySpecification(QueryExpression queryExpression) =>
            queryExpression switch
            {
                QueryParenthesisExpression parenthesis => OutermostQuerySpecification(parenthesis.QueryExpression),
                QuerySpecification spec => spec,
                _ => null, // A top-level UNION/EXCEPT/INTERSECT declines rather than guessing which branch's TOP/ORDER BY matters.
            };

        private static bool IsLiteralValue(ScalarExpression expression, string value) =>
            // TOP (100)'s own required parens wrap the literal in a ParenthesisExpression -
            // oracle/parser-confirmed directly (TopRowFilter.Expression is never the bare literal).
            Unwrap(expression) is IntegerLiteral { Value: var v } && v == value;

        private static ScalarExpression Unwrap(ScalarExpression expression) =>
            expression is ParenthesisExpression parenthesis ? Unwrap(parenthesis.Expression) : expression;
    }
}
