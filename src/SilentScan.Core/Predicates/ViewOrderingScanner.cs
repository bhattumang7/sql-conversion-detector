using Microsoft.SqlServer.TransactSql.ScriptDom;
using SilentScan.Core.Catalog;
using SilentScan.Core.Parsing;
using SilentScan.Core.Common;

namespace SilentScan.Core.Predicates;

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

        }

        private static QuerySpecification? OutermostQuerySpecification(QueryExpression queryExpression) =>
            queryExpression switch
            {
                QueryParenthesisExpression parenthesis => OutermostQuerySpecification(parenthesis.QueryExpression),
                QuerySpecification spec => spec,
                _ => null,            };

        private static bool IsLiteralValue(ScalarExpression expression, string value) =>
            Unwrap(expression) is IntegerLiteral { Value: var v } && v == value;

        private static ScalarExpression Unwrap(ScalarExpression expression) =>
            expression is ParenthesisExpression parenthesis ? Unwrap(parenthesis.Expression) : expression;
    }
}
