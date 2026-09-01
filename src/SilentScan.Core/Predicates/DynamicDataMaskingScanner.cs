using Microsoft.SqlServer.TransactSql.ScriptDom;
using SilentScan.Core.Catalog;
using SilentScan.Core.Lineage;
using SilentScan.Core.Parsing;

namespace SilentScan.Core.Predicates;

public static class DynamicDataMaskingScanner
{
    private static readonly IReadOnlyDictionary<string, ResolvedRelation> EmptyResolvedViews = new Dictionary<string, ResolvedRelation>();

    public static IReadOnlyList<DynamicDataMaskingFinding> Scan(SqlParseResult parseResult, DatabaseCatalog catalog)
    {
        var rule = CreateRule(parseResult.SourcePath, catalog);
        var walker = new ModuleWalker(parseResult.SourcePath, catalog, EmptyResolvedViews, rules: [rule]);
        parseResult.Fragment.Accept(walker);
        return Harvest(rule);
    }

    internal static Rule CreateRule(string sourcePath, DatabaseCatalog catalog) => new(sourcePath, catalog);

    internal static IReadOnlyList<DynamicDataMaskingFinding> Harvest(Rule rule) =>
        [
            .. rule.Findings
                .OrderBy(f => f.SourcePath, StringComparer.Ordinal)
                .ThenBy(f => f.Line)
                .ThenBy(f => f.Column),
        ];

    internal sealed class Rule(string sourcePath, DatabaseCatalog catalog) : IModuleRule
    {
        private readonly HashSet<(TSqlFragment Node, string Table, string Column)> _reported = [];

        public List<DynamicDataMaskingFinding> Findings { get; } = [];

        public void OnBooleanComparisonExpression(BooleanComparisonExpression node, ModuleWalker walker) =>
            InspectOperands(node, walker, "comparison", node.FirstExpression, node.SecondExpression);

        public void OnBooleanTernaryExpression(BooleanTernaryExpression node, ModuleWalker walker) =>
            InspectOperands(node, walker, "BETWEEN range comparison", node.FirstExpression, node.SecondExpression, node.ThirdExpression);

        public void OnLikePredicate(LikePredicate node, ModuleWalker walker) =>
            InspectOperands(node, walker, "LIKE pattern match", node.FirstExpression, node.SecondExpression);

        public void OnInPredicate(InPredicate node, ModuleWalker walker) =>
            InspectOperands(node, walker, "IN list comparison", node.Expression);

        public void OnEnterGroupByClause(GroupByClause node, ModuleWalker walker)
        {
            var scopeChain = walker.CurrentScopeChain();
            foreach (var spec in node.GroupingSpecifications.OfType<ExpressionGroupingSpecification>())
            {
                InspectOperand(spec, walker, scopeChain, "GROUP BY", spec.Expression);
            }
        }

        public void OnEnterOrderByClause(OrderByClause node, ModuleWalker walker)
        {
            var scopeChain = walker.CurrentScopeChain();
            foreach (var element in node.OrderByElements)
            {
                InspectOperand(element, walker, scopeChain, "ORDER BY", element.Expression);
            }
        }

        public void OnEnterSelectScalarExpression(SelectScalarExpression node, ModuleWalker walker)
        {
            var expression = Unwrap(node.Expression);
            if (expression is null or ColumnReferenceExpression)
            {
                return;
            }

            var scopeChain = walker.CurrentScopeChain();
            var sink = new HashSet<(string Table, string Column)>(TableColumnKeyComparer.For(catalog));
            expression.Accept(new BaseColumnResolver.ColumnReferenceCollector(sourcePath, scopeChain, sink, catalog));

            foreach (var (table, columnName) in sink)
            {
                var catalogColumn = catalog.Find(table, walker.CurrentProcScope)?.FindColumn(columnName, catalog.IdentifierComparer);
                if (catalogColumn is { IsMasked: true })
                {
                    Report(node, table, catalogColumn, "SELECT list expression", DynamicDataMaskingFindingKind.ComputedExpressionCollapse);
                }
            }
        }

        private void InspectOperands(TSqlFragment node, ModuleWalker walker, string context, params ScalarExpression?[] operands)
        {
            var scopeChain = walker.CurrentScopeChain();
            foreach (var operand in operands)
            {
                InspectOperand(node, walker, scopeChain, context, operand);
            }
        }

        private void InspectOperand(TSqlFragment node, ModuleWalker walker, ScopeChain scopeChain, string context, ScalarExpression? operand)
        {
            if (Unwrap(operand) is not ColumnReferenceExpression columnRef)
            {
                return;
            }

            if (walker.ResolveCatalogColumn(columnRef, scopeChain) is { Column.IsMasked: true } resolved)
            {
                Report(node, resolved.TableQualifiedName, resolved.Column, context, DynamicDataMaskingFindingKind.PredicateExposure);
            }
        }

        private void Report(TSqlFragment node, string table, CatalogColumn column, string context, DynamicDataMaskingFindingKind kind)
        {
            if (!_reported.Add((node, table, column.Name)))
            {
                return;
            }

            Findings.Add(new DynamicDataMaskingFinding(
                table, column.Name, column.MaskingFunctionName ?? "default", context,
                sourcePath, node.StartLine, node.StartColumn, kind));
        }

        private static ScalarExpression? Unwrap(ScalarExpression? expression)
        {
            while (expression is ParenthesisExpression parenthesized)
            {
                expression = parenthesized.Expression;
            }

            return expression;
        }
    }
}
