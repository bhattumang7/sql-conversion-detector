using Microsoft.SqlServer.TransactSql.ScriptDom;
using SilentScan.Core.Catalog;
using SilentScan.Core.Lineage;
using SilentScan.Core.Parsing;
using SilentScan.Core.Predicates.Normalization;

namespace SilentScan.Core.Predicates;

public static class CheckConstraintPredicateContradictionScanner
{
    private static readonly IReadOnlyDictionary<string, ResolvedRelation> EmptyResolvedViews = new Dictionary<string, ResolvedRelation>();

    public static IReadOnlyList<CheckConstraintPredicateContradictionFinding> Scan(SqlParseResult parseResult, DatabaseCatalog catalog)
    {
        var rule = CreateRule(parseResult.SourcePath, catalog);
        var walker = new ModuleWalker(parseResult.SourcePath, catalog, EmptyResolvedViews, rules: [rule]);
        parseResult.Fragment.Accept(walker);
        return Harvest(rule);
    }

    internal static Rule CreateRule(string sourcePath, DatabaseCatalog catalog) => new(sourcePath, catalog);

    internal static IReadOnlyList<CheckConstraintPredicateContradictionFinding> Harvest(Rule rule) =>
        [
            .. rule.Findings
                .OrderBy(f => f.SourcePath, StringComparer.Ordinal)
                .ThenBy(f => f.Line)
                .ThenBy(f => f.Column),
        ];

    internal sealed class Rule : IModuleRule
    {
        private readonly Dictionary<(string Table, string Column), List<(string ConstraintName, NumericValueRangeSet Domain)>> _domains;

        public List<CheckConstraintPredicateContradictionFinding> Findings { get; } = [];

        public Rule(string sourcePath, DatabaseCatalog catalog)
        {
            SourcePath = sourcePath;
            _domains = BuildDomains(catalog);
        }

        private string SourcePath { get; }

        public void OnEnterQuerySpecificationScope(QuerySpecification node, ScopeChain scopeChain, ModuleWalker walker) =>
            Inspect(node.WhereClause?.SearchCondition, scopeChain, walker);

        public void OnEnterUpdateStatementScope(UpdateStatement node, ScopeChain scopeChain, ModuleWalker walker) =>
            Inspect(node.UpdateSpecification.WhereClause?.SearchCondition, scopeChain, walker);

        public void OnEnterDeleteStatementScope(DeleteStatement node, ScopeChain scopeChain, ModuleWalker walker) =>
            Inspect(node.DeleteSpecification.WhereClause?.SearchCondition, scopeChain, walker);

        private void Inspect(BooleanExpression? searchCondition, ScopeChain scopeChain, ModuleWalker walker)
        {
            if (searchCondition is null)
            {
                return;
            }

            if (Classify(searchCondition, scopeChain, walker) is not { } evidence)
            {
                return;
            }

            Findings.Add(new CheckConstraintPredicateContradictionFinding(
                evidence.Kind, evidence.TableQualifiedName, evidence.ColumnName, evidence.ConstraintName,
                SourcePath, evidence.Node.StartLine, evidence.Node.StartColumn));
        }

        private Evidence? Classify(BooleanExpression node, ScopeChain scopeChain, ModuleWalker walker)
        {
            switch (node)
            {
                case BooleanParenthesisExpression paren:
                    return Classify(paren.Expression, scopeChain, walker);

                case BooleanBinaryExpression { BinaryExpressionType: BooleanBinaryExpressionType.And } and:
                    return Classify(and.FirstExpression, scopeChain, walker) ?? Classify(and.SecondExpression, scopeChain, walker);

                case BooleanBinaryExpression { BinaryExpressionType: BooleanBinaryExpressionType.Or } or_:
                {
                    var left = Classify(or_.FirstExpression, scopeChain, walker);
                    if (left is null)
                    {
                        return null;
                    }

                    var right = Classify(or_.SecondExpression, scopeChain, walker);
                    return right is null ? null : left;
                }

                case BooleanTernaryExpression { TernaryExpressionType: BooleanTernaryExpressionType.Between } between:
                    return ClassifyBetween(between, scopeChain, walker);

                case BooleanComparisonExpression cmp:
                    return ClassifyComparison(cmp, scopeChain, walker);

                case BooleanIsNullExpression { IsNot: false } isNull:
                    return ClassifyIsNull(isNull, scopeChain, walker);

                default:
                    return null;
            }
        }

        private Evidence? ClassifyComparison(BooleanComparisonExpression cmp, ScopeChain scopeChain, ModuleWalker walker)
        {
            if (TryClassifyColumnSide(cmp.FirstExpression, cmp.SecondExpression, literalOnRight: true, cmp.ComparisonType, cmp, scopeChain, walker) is { } fromFirst)
            {
                return fromFirst;
            }

            return TryClassifyColumnSide(cmp.SecondExpression, cmp.FirstExpression, literalOnRight: false, cmp.ComparisonType, cmp, scopeChain, walker);
        }

        private Evidence? TryClassifyColumnSide(
            ScalarExpression columnSide, ScalarExpression literalSide, bool literalOnRight, BooleanComparisonType comparisonType,
            TSqlFragment locationNode, ScopeChain scopeChain, ModuleWalker walker)
        {
            if (columnSide is not ColumnReferenceExpression columnRef)
            {
                return null;
            }

            if (walker.ResolveCatalogColumn(columnRef, scopeChain) is not { } resolved)
            {
                return null;
            }

            if (!_domains.TryGetValue((resolved.TableQualifiedName, resolved.Column.Name), out var domains))
            {
                return null;
            }

            var leafRange = CheckConstraintDomainFolder.TryRangeSet(comparisonType, literalSide, literalOnRight);
            if (leafRange is null)
            {
                return null;
            }

            foreach (var (constraintName, domain) in domains)
            {
                if (leafRange.Intersect(domain).IsEmpty)
                {
                    return new Evidence(
                        resolved.TableQualifiedName, resolved.Column.Name, constraintName,
                        CheckConstraintPredicateContradictionKind.CheckConstraintInterval, locationNode);
                }
            }

            return null;
        }

        private Evidence? ClassifyBetween(BooleanTernaryExpression between, ScopeChain scopeChain, ModuleWalker walker)
        {
            if (between.FirstExpression is not ColumnReferenceExpression columnRef)
            {
                return null;
            }

            if (walker.ResolveCatalogColumn(columnRef, scopeChain) is not { } resolved)
            {
                return null;
            }

            if (!_domains.TryGetValue((resolved.TableQualifiedName, resolved.Column.Name), out var domains))
            {
                return null;
            }

            if (CheckConstraintDomainFolder.TryGetNumericLiteral(between.SecondExpression) is not { } lower
                || CheckConstraintDomainFolder.TryGetNumericLiteral(between.ThirdExpression) is not { } upper)
            {
                return null;
            }

            var leafRange = NumericValueRangeSet.ForGreaterThanOrEqual(lower).Intersect(NumericValueRangeSet.ForLessThanOrEqual(upper));
            foreach (var (constraintName, domain) in domains)
            {
                if (leafRange.Intersect(domain).IsEmpty)
                {
                    return new Evidence(
                        resolved.TableQualifiedName, resolved.Column.Name, constraintName,
                        CheckConstraintPredicateContradictionKind.CheckConstraintInterval, between);
                }
            }

            return null;
        }

        private static Evidence? ClassifyIsNull(BooleanIsNullExpression isNull, ScopeChain scopeChain, ModuleWalker walker)
        {
            if (isNull.Expression is not ColumnReferenceExpression columnRef)
            {
                return null;
            }

            if (walker.ResolveCatalogColumn(columnRef, scopeChain) is not { } resolved)
            {
                return null;
            }

            if (walker.ResolveColumnFacts(columnRef, scopeChain).IsNotNull != true)
            {
                return null;
            }

            return new Evidence(
                resolved.TableQualifiedName, resolved.Column.Name, ConstraintName: null,
                CheckConstraintPredicateContradictionKind.NotNullConstraint, isNull);
        }

        private static Dictionary<(string Table, string Column), List<(string ConstraintName, NumericValueRangeSet Domain)>> BuildDomains(DatabaseCatalog catalog)
        {
            var domains = new Dictionary<(string, string), List<(string, NumericValueRangeSet)>>(TableColumnKeyComparer.For(catalog));

            foreach (var check in catalog.CheckConstraints)
            {
                if (check.IsDisabled || check.IsNotTrusted || string.IsNullOrWhiteSpace(check.DefinitionText))
                {
                    continue;
                }

                var condition = CheckConstraintScanner.TryParse(check.DefinitionText, catalog.CompatibilityLevel);
                if (condition is null)
                {
                    continue;
                }

                var referencedColumns = new HashSet<string>(catalog.IdentifierComparer);
                condition.Accept(new ColumnNameCollector(referencedColumns));
                if (referencedColumns.Count != 1)
                {
                    continue;
                }

                var columnName = referencedColumns.Single();
                var range = CheckConstraintDomainFolder.TryBuildRangeSet(condition, columnName, catalog.IdentifierComparer);
                if (range is null)
                {
                    continue;
                }

                var key = (check.TableQualifiedName, columnName);
                if (!domains.TryGetValue(key, out var list))
                {
                    list = [];
                    domains[key] = list;
                }

                list.Add((check.ConstraintName, range));
            }

            return domains;
        }

        private readonly record struct Evidence(
            string TableQualifiedName, string ColumnName, string? ConstraintName,
            CheckConstraintPredicateContradictionKind Kind, TSqlFragment Node);

        private sealed class ColumnNameCollector(HashSet<string> names) : TSqlFragmentVisitor
        {
            public override void ExplicitVisit(ColumnReferenceExpression node)
            {
                var identifiers = node.MultiPartIdentifier?.Identifiers;
                if (identifiers is { Count: > 0 })
                {
                    names.Add(identifiers[^1].Value);
                }

                base.ExplicitVisit(node);
            }
        }
    }
}
