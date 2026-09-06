using Microsoft.SqlServer.TransactSql.ScriptDom;
using SilentScan.Core.Catalog;
using SilentScan.Core.Common;
using SilentScan.Core.Lineage;
using SilentScan.Core.Parsing;

namespace SilentScan.Core.Predicates;

public static class GroupByValidityScanner
{
    private static readonly HashSet<string> AggregateFunctionNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "SUM", "COUNT", "COUNT_BIG", "AVG", "MIN", "MAX",
        "STDEV", "STDEVP", "VAR", "VARP",
        "GROUPING", "GROUPING_ID", "STRING_AGG", "CHECKSUM_AGG", "APPROX_COUNT_DISTINCT",
    };

    public static IReadOnlyList<GroupByValidityFinding> Scan(SqlParseResult parseResult)
    {
        var rule = CreateRule(parseResult.SourcePath);
        var walker = new ModuleWalker(parseResult.SourcePath, new DatabaseCatalog(), EmptyResolvedViews, rules: [rule]);
        parseResult.Fragment.Accept(walker);
        return Harvest(rule);
    }

    internal static Rule CreateRule(string sourcePath) => new(sourcePath);

    internal static IReadOnlyList<GroupByValidityFinding> Harvest(Rule rule) =>
        [
            .. rule.Findings
                .OrderBy(f => f.SourcePath, StringComparer.Ordinal)
                .ThenBy(f => f.Line)
                .ThenBy(f => f.Column),
        ];

    private static readonly IReadOnlyDictionary<string, ResolvedRelation> EmptyResolvedViews = new Dictionary<string, ResolvedRelation>();

    internal sealed class Rule(string sourcePath) : IModuleRule
    {
        public List<GroupByValidityFinding> Findings { get; } = [];

        public void OnEnterQuerySpecificationScope(QuerySpecification node, ScopeChain scopeChain, ModuleWalker walker)
        {
            if (node.GroupByClause is not { GroupByOption: GroupByOption.None } groupBy
                || groupBy.GroupingSpecifications.Any(spec => spec is not ExpressionGroupingSpecification))
            {
                return;
            }

            var groupedTexts = new HashSet<string>(
                groupBy.GroupingSpecifications
                    .OfType<ExpressionGroupingSpecification>()
                    .Select(spec => FragmentTextRenderer.Render(spec.Expression)),
                StringComparer.Ordinal);

            foreach (var element in node.SelectElements.OfType<SelectScalarExpression>())
            {
                CheckScalar(element.Expression, groupedTexts, GroupByValidityFindingKind.SelectList);
            }

            if (node.HavingClause?.SearchCondition is { } having)
            {
                CheckBoolean(having, groupedTexts, GroupByValidityFindingKind.Having);
            }

            if (node.OrderByClause is { } orderBy)
            {
                foreach (var element in orderBy.OrderByElements)
                {
                    CheckScalar(element.Expression, groupedTexts, GroupByValidityFindingKind.OrderBy);
                }
            }
        }

        private void CheckBoolean(BooleanExpression node, HashSet<string> groupedTexts, GroupByValidityFindingKind kind)
        {
            switch (node)
            {
                case BooleanBinaryExpression binary:
                    CheckBoolean(binary.FirstExpression, groupedTexts, kind);
                    CheckBoolean(binary.SecondExpression, groupedTexts, kind);
                    break;

                case BooleanNotExpression not:
                    CheckBoolean(not.Expression, groupedTexts, kind);
                    break;

                case BooleanParenthesisExpression paren:
                    CheckBoolean(paren.Expression, groupedTexts, kind);
                    break;

                case BooleanComparisonExpression cmp:
                    CheckScalar(cmp.FirstExpression, groupedTexts, kind);
                    CheckScalar(cmp.SecondExpression, groupedTexts, kind);
                    break;

                case BooleanTernaryExpression ternary:
                    CheckScalar(ternary.FirstExpression, groupedTexts, kind);
                    CheckScalar(ternary.SecondExpression, groupedTexts, kind);
                    CheckScalar(ternary.ThirdExpression, groupedTexts, kind);
                    break;

                case BooleanIsNullExpression isNull:
                    CheckScalar(isNull.Expression, groupedTexts, kind);
                    break;

                case LikePredicate like:
                    CheckScalar(like.FirstExpression, groupedTexts, kind);
                    CheckScalar(like.SecondExpression, groupedTexts, kind);
                    break;

                case InPredicate { Subquery: null } inPredicate:
                    CheckScalar(inPredicate.Expression, groupedTexts, kind);
                    foreach (var value in inPredicate.Values)
                    {
                        CheckScalar(value, groupedTexts, kind);
                    }

                    break;

                case SubqueryComparisonPredicate subqueryComparison:
                    CheckScalar(subqueryComparison.Expression, groupedTexts, kind);
                    break;
            }
        }

        private void CheckScalar(ScalarExpression node, HashSet<string> groupedTexts, GroupByValidityFindingKind kind)
        {
            if (groupedTexts.Contains(FragmentTextRenderer.Render(node)))
            {
                return;
            }

            switch (node)
            {
                case ColumnReferenceExpression:
                    Findings.Add(new GroupByValidityFinding(kind, FragmentTextRenderer.Render(node), sourcePath, node.StartLine, node.StartColumn));
                    break;

                case BinaryExpression binary:
                    CheckScalar(binary.FirstExpression, groupedTexts, kind);
                    CheckScalar(binary.SecondExpression, groupedTexts, kind);
                    break;

                case UnaryExpression unary:
                    CheckScalar(unary.Expression, groupedTexts, kind);
                    break;

                case ParenthesisExpression paren:
                    CheckScalar(paren.Expression, groupedTexts, kind);
                    break;

                case CastCall castCall:
                    CheckScalar(castCall.Parameter, groupedTexts, kind);
                    break;

                case ConvertCall convertCall:
                    CheckScalar(convertCall.Parameter, groupedTexts, kind);
                    break;

                case TryCastCall tryCastCall:
                    CheckScalar(tryCastCall.Parameter, groupedTexts, kind);
                    break;

                case TryConvertCall tryConvertCall:
                    CheckScalar(tryConvertCall.Parameter, groupedTexts, kind);
                    break;

                case FunctionCall functionCall when AggregateFunctionNames.Contains(functionCall.FunctionName.Value):
                    break;

                case FunctionCall functionCall:
                    foreach (var parameter in functionCall.Parameters)
                    {
                        CheckScalar(parameter, groupedTexts, kind);
                    }

                    break;

                case CoalesceExpression coalesce:
                    foreach (var expression in coalesce.Expressions)
                    {
                        CheckScalar(expression, groupedTexts, kind);
                    }

                    break;

                case NullIfExpression nullIf:
                    CheckScalar(nullIf.FirstExpression, groupedTexts, kind);
                    CheckScalar(nullIf.SecondExpression, groupedTexts, kind);
                    break;

                case IIfCall iif:
                    CheckBoolean(iif.Predicate, groupedTexts, kind);
                    CheckScalar(iif.ThenExpression, groupedTexts, kind);
                    CheckScalar(iif.ElseExpression, groupedTexts, kind);
                    break;

                case SearchedCaseExpression searchedCase:
                    foreach (var whenClause in searchedCase.WhenClauses)
                    {
                        CheckBoolean(whenClause.WhenExpression, groupedTexts, kind);
                        CheckScalar(whenClause.ThenExpression, groupedTexts, kind);
                    }

                    if (searchedCase.ElseExpression is { } searchedElse)
                    {
                        CheckScalar(searchedElse, groupedTexts, kind);
                    }

                    break;

                case SimpleCaseExpression simpleCase:
                    CheckScalar(simpleCase.InputExpression, groupedTexts, kind);
                    foreach (var whenClause in simpleCase.WhenClauses)
                    {
                        CheckScalar(whenClause.WhenExpression, groupedTexts, kind);
                        CheckScalar(whenClause.ThenExpression, groupedTexts, kind);
                    }

                    if (simpleCase.ElseExpression is { } simpleElse)
                    {
                        CheckScalar(simpleElse, groupedTexts, kind);
                    }

                    break;
            }
        }
    }
}
