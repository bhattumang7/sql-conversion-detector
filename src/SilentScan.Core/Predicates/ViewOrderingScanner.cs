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
            var outer = OutermostQueryExpression(queryExpression);
            var target = outer is BinaryQueryExpression binary ? TrailingOrderedQueryExpression(binary) : outer;
            if (target is null || target.OrderByClause is null)
            {
                return;
            }

            if (ReferenceEquals(target, outer) && target is QuerySpecification { TopRowFilter: { } top })
            {
                var isHundredPercent = top.Percent && TopPercentLiteralHelper.IsHundredPercentLiteral(top.Expression);
                if (isHundredPercent)
                {
                    Findings.Add(new ViewOrderingFinding(
                        ViewOrderingFindingKind.TopPercentOrderByNeverLimits, qualifiedName, sourcePath,
                        target.StartLine, target.StartColumn, FindingConfidence.High));
                }
                else
                {
                    Findings.Add(new ViewOrderingFinding(
                        ViewOrderingFindingKind.OrderByNotGuaranteedToConsumer, qualifiedName, sourcePath,
                        target.StartLine, target.StartColumn, FindingConfidence.Low));
                }
            }
            else if (target.OffsetClause is not null)
            {
                Findings.Add(new ViewOrderingFinding(
                    ViewOrderingFindingKind.OrderByNotGuaranteedToConsumer, qualifiedName, sourcePath,
                    target.StartLine, target.StartColumn, FindingConfidence.Low));
            }

        }

        private static QueryExpression OutermostQueryExpression(QueryExpression queryExpression) =>
            queryExpression is QueryParenthesisExpression parenthesis
                ? OutermostQueryExpression(parenthesis.QueryExpression)
                : queryExpression;

        private static QueryExpression? TrailingOrderedQueryExpression(QueryExpression queryExpression)
        {
            if (queryExpression.OrderByClause is not null)
            {
                return queryExpression;
            }

            return queryExpression is BinaryQueryExpression binary
                ? TrailingOrderedQueryExpression(binary.SecondQueryExpression)
                : null;
        }
    }
}
