using Microsoft.SqlServer.TransactSql.ScriptDom;
using SilentScan.Core.Catalog;
using SilentScan.Core.Lineage;
using SilentScan.Core.Parsing;
using SilentScan.Core.TypeInference;

namespace SilentScan.Core.Predicates;

public static class OperandComparabilityScanner
{
    private static readonly IReadOnlyDictionary<string, ResolvedRelation> EmptyResolvedViews = new Dictionary<string, ResolvedRelation>();

    public static IReadOnlyList<OperandComparabilityFinding> Scan(SqlParseResult parseResult, DatabaseCatalog catalog)
    {
        var rule = new Rule(parseResult.SourcePath, catalog);
        var walker = new ModuleWalker(parseResult.SourcePath, catalog, EmptyResolvedViews, ledger: null, currentProcScope: null, callerScopeByCalleeScope: null, rules: [rule]);
        parseResult.Fragment.Accept(walker);
        return
        [
            .. rule.Findings
                .OrderBy(f => f.SourcePath, StringComparer.Ordinal)
                .ThenBy(f => f.Line)
                .ThenBy(f => f.Column),
        ];
    }

    private sealed class Rule(string sourcePath, DatabaseCatalog catalog) : IModuleRule
    {
        public List<OperandComparabilityFinding> Findings { get; } = [];

        public void OnEnterQuerySpecificationScope(QuerySpecification node, ScopeChain scopeChain, ModuleWalker walker)
        {
            ModuleWalker.InspectAllPredicateLocations(node, scopeChain, InspectSearchCondition);

            if (node.GroupByClause is { } groupBy)
            {
                foreach (var grouping in groupBy.GroupingSpecifications.OfType<ExpressionGroupingSpecification>())
                {
                    InspectMembership(grouping.Expression, scopeChain, OperandComparabilityContext.GroupBy);
                }
            }

            if (node.OrderByClause is { OrderByElements.Count: > 0 } orderBy)
            {
                foreach (var element in orderBy.OrderByElements)
                {
                    InspectMembership(element.Expression, scopeChain, OperandComparabilityContext.OrderBy);
                }
            }

            foreach (var expression in node.SelectElements.OfType<SelectScalarExpression>().Select(scalar => scalar.Expression))
            {
                if (node.UniqueRowFilter == UniqueRowFilter.Distinct)
                {
                    InspectMembership(expression, scopeChain, OperandComparabilityContext.Distinct);
                }

                InspectExpressionTree(expression, scopeChain);
            }
        }

        public void OnEnterUpdateStatementScope(UpdateStatement node, ScopeChain scopeChain, ModuleWalker walker) =>
            ModuleWalker.InspectAllPredicateLocations(node, scopeChain, InspectSearchCondition);

        public void OnEnterDeleteStatementScope(DeleteStatement node, ScopeChain scopeChain, ModuleWalker walker) =>
            ModuleWalker.InspectAllPredicateLocations(node, scopeChain, InspectSearchCondition);

        private void InspectSearchCondition(
            BooleanExpression searchCondition,
            IReadOnlyList<(IReadOnlyDictionary<string, ScopeEntry> ByAlias, IReadOnlyList<ScopeEntry> Ordered)> scopeChain) =>
            InspectExpressionTree(searchCondition, scopeChain);

        private void InspectExpressionTree(
            TSqlFragment fragment,
            IReadOnlyList<(IReadOnlyDictionary<string, ScopeEntry> ByAlias, IReadOnlyList<ScopeEntry> Ordered)> scopeChain)
        {
            var collector = new ComparisonCollector();
            fragment.Accept(collector);

            foreach (var comparison in collector.Comparisons)
            {
                InspectComparison(comparison, scopeChain);
            }

            foreach (var inPredicate in collector.InPredicates)
            {
                InspectMembership(inPredicate.Expression, scopeChain, OperandComparabilityContext.In);
            }

            foreach (var between in collector.Betweens)
            {
                InspectBetween(between, scopeChain);
            }

            foreach (var nullIf in collector.NullIfs)
            {
                InspectNullIf(nullIf, scopeChain);
            }
        }

        private void InspectBetween(
            BooleanTernaryExpression between,
            IReadOnlyList<(IReadOnlyDictionary<string, ScopeEntry> ByAlias, IReadOnlyList<ScopeEntry> Ordered)> scopeChain)
        {
            foreach (var side in new[] { between.FirstExpression, between.SecondExpression, between.ThirdExpression })
            {
                if (TryClassify(side, scopeChain) is not { } match)
                {
                    continue;
                }

                Add(match, OperandComparabilityContext.Between, operatorText: null, between.StartLine, between.StartColumn);
                return;
            }
        }

        private void InspectNullIf(
            NullIfExpression nullIf,
            IReadOnlyList<(IReadOnlyDictionary<string, ScopeEntry> ByAlias, IReadOnlyList<ScopeEntry> Ordered)> scopeChain)
        {
            foreach (var side in new[] { nullIf.FirstExpression, nullIf.SecondExpression })
            {
                if (TryClassify(side, scopeChain) is not { } match)
                {
                    continue;
                }

                Add(match, OperandComparabilityContext.NullIf, operatorText: null, nullIf.StartLine, nullIf.StartColumn);
                return;
            }
        }

        private void InspectComparison(
            BooleanComparisonExpression comparison,
            IReadOnlyList<(IReadOnlyDictionary<string, ScopeEntry> ByAlias, IReadOnlyList<ScopeEntry> Ordered)> scopeChain)
        {
            var operatorText = OperatorText(comparison.ComparisonType);
            foreach (var side in new[] { comparison.FirstExpression, comparison.SecondExpression })
            {
                if (TryClassify(side, scopeChain) is not { } match)
                {
                    continue;
                }

                Add(match, OperandComparabilityContext.Comparison, operatorText, comparison.StartLine, comparison.StartColumn);
                return;
            }
        }

        private void InspectMembership(
            ScalarExpression expression,
            IReadOnlyList<(IReadOnlyDictionary<string, ScopeEntry> ByAlias, IReadOnlyList<ScopeEntry> Ordered)> scopeChain,
            OperandComparabilityContext context)
        {
            if (TryClassify(expression, scopeChain) is not { } match)
            {
                return;
            }

            Add(match, context, operatorText: null, expression.StartLine, expression.StartColumn);
        }

        private (ColumnProvenance.BaseColumn Resolved, OperandComparabilityFindingKind Kind)? TryClassify(
            ScalarExpression expression,
            IReadOnlyList<(IReadOnlyDictionary<string, ScopeEntry> ByAlias, IReadOnlyList<ScopeEntry> Ordered)> scopeChain)
        {
            if (BaseColumnResolver.ResolveBaseColumn(expression, sourcePath, scopeChain, catalog) is not { } resolved)
            {
                return null;
            }

            return resolved.Type?.Category switch
            {
                SqlTypeCategory.Xml => (resolved, OperandComparabilityFindingKind.Xml),
                SqlTypeCategory.Text or SqlTypeCategory.NText or SqlTypeCategory.Image => (resolved, OperandComparabilityFindingKind.LegacyLargeObject),
                _ => null,
            };
        }

        private void Add(
            (ColumnProvenance.BaseColumn Resolved, OperandComparabilityFindingKind Kind) match,
            OperandComparabilityContext context, string? operatorText, int line, int column)
        {
            Findings.Add(new OperandComparabilityFinding(
                match.Resolved.TableQualifiedName,
                match.Resolved.ColumnName,
                match.Resolved.Type!.ToString(),
                match.Kind,
                context,
                operatorText,
                sourcePath,
                line,
                column));
        }

        private static string OperatorText(BooleanComparisonType comparisonType) => comparisonType switch
        {
            BooleanComparisonType.Equals => "=",
            BooleanComparisonType.GreaterThan => ">",
            BooleanComparisonType.NotGreaterThan => "!>",
            BooleanComparisonType.LessThan => "<",
            BooleanComparisonType.NotLessThan => "!<",
            BooleanComparisonType.GreaterThanOrEqualTo => ">=",
            BooleanComparisonType.LessThanOrEqualTo => "<=",
            BooleanComparisonType.NotEqualToBrackets => "<>",
            BooleanComparisonType.NotEqualToExclamation => "<>",
            _ => comparisonType.ToString(),
        };

        private sealed class ComparisonCollector : TSqlFragmentVisitor
        {
            public List<BooleanComparisonExpression> Comparisons { get; } = [];

            public List<InPredicate> InPredicates { get; } = [];

            public List<BooleanTernaryExpression> Betweens { get; } = [];

            public List<NullIfExpression> NullIfs { get; } = [];

            public override void ExplicitVisit(BooleanComparisonExpression node)
            {
                Comparisons.Add(node);
                base.ExplicitVisit(node);
            }

            public override void ExplicitVisit(InPredicate node)
            {
                InPredicates.Add(node);
                base.ExplicitVisit(node);
            }

            public override void ExplicitVisit(BooleanTernaryExpression node)
            {
                if (node.TernaryExpressionType is BooleanTernaryExpressionType.Between or BooleanTernaryExpressionType.NotBetween)
                {
                    Betweens.Add(node);
                }

                base.ExplicitVisit(node);
            }

            public override void ExplicitVisit(NullIfExpression node)
            {
                NullIfs.Add(node);
                base.ExplicitVisit(node);
            }

            public override void ExplicitVisit(QuerySpecification node)
            {
                _ = node;
            }
        }
    }
}
