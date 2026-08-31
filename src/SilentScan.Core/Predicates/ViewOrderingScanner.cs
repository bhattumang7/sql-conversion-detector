using Microsoft.SqlServer.TransactSql.ScriptDom;
using SilentScan.Core.Catalog;
using SilentScan.Core.Lineage;
using SilentScan.Core.Parsing;
using SilentScan.Core.Common;

namespace SilentScan.Core.Predicates;

public static class ViewOrderingScanner
{
    public static IReadOnlyList<ViewOrderingFinding> Scan(SqlParseResult parseResult)
    {
        var rule = CreateRule(parseResult.SourcePath);
        var walker = new ModuleWalker(parseResult.SourcePath, new DatabaseCatalog(), EmptyResolvedViews, rules: [rule]);
        parseResult.Fragment.Accept(walker);
    return Harvest(rule);
    }
    internal static Rule CreateRule(string sourcePath) => new(sourcePath);

    internal static IReadOnlyList<ViewOrderingFinding> Harvest(Rule rule) =>
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
        public List<ViewOrderingFinding> Findings { get; } = [];

        public void OnEnterCreateViewStatement(CreateViewStatement node, ModuleWalker walker) => Inspect(SchemaObjectNameHelper.Qualify(node.SchemaObjectName), node.SelectStatement.QueryExpression);

        public void OnEnterAlterViewStatement(AlterViewStatement node, ModuleWalker walker) => Inspect(SchemaObjectNameHelper.Qualify(node.SchemaObjectName), node.SelectStatement.QueryExpression);

        public void OnEnterCreateOrAlterViewStatement(CreateOrAlterViewStatement node, ModuleWalker walker) => Inspect(SchemaObjectNameHelper.Qualify(node.SchemaObjectName), node.SelectStatement.QueryExpression);

        public void OnEnterCreateFunctionStatement(CreateFunctionStatement node, ModuleWalker walker) => InspectFunction(node.Name, node.ReturnType);

        public void OnEnterAlterFunctionStatement(AlterFunctionStatement node, ModuleWalker walker) => InspectFunction(node.Name, node.ReturnType);

        public void OnEnterCreateOrAlterFunctionStatement(CreateOrAlterFunctionStatement node, ModuleWalker walker) => InspectFunction(node.Name, node.ReturnType);

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
                var isHundredPercent = top.Percent && IsHundredPercentLiteral(top.Expression);
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
                _ => null,
            };

        private static bool IsHundredPercentLiteral(ScalarExpression expression) =>
            Unwrap(expression) switch
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
