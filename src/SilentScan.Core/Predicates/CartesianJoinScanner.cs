using System.Globalization;
using Microsoft.SqlServer.TransactSql.ScriptDom;
using SilentScan.Core.Catalog;
using SilentScan.Core.Lineage;
using SilentScan.Core.Parsing;
using SilentScan.Core.Common;
using SilentScan.Core.Predicates.Normalization;

namespace SilentScan.Core.Predicates;

public static class CartesianJoinScanner
{
    public static IReadOnlyList<CartesianJoinFinding> Scan(SqlParseResult parseResult, DatabaseCatalog? catalog = null)
    {
        var rule = CreateRule(parseResult.SourcePath, catalog);
        var walker = new ModuleWalker(parseResult.SourcePath, catalog ?? new DatabaseCatalog(), EmptyResolvedViews, rules: [rule]);
        parseResult.Fragment.Accept(walker);
    return Harvest(rule);
    }
    internal static Rule CreateRule(string sourcePath, DatabaseCatalog? catalog = null) => new(sourcePath, catalog?.IdentifierComparer ?? StringComparer.OrdinalIgnoreCase);

    internal static IReadOnlyList<CartesianJoinFinding> Harvest(Rule rule) =>
            [
            .. rule.Findings
                .OrderBy(f => f.SourcePath, StringComparer.Ordinal)
                .ThenBy(f => f.Line)
                .ThenBy(f => f.Column),
        ];


    private static readonly IReadOnlyDictionary<string, ResolvedRelation> EmptyResolvedViews = new Dictionary<string, ResolvedRelation>();

    internal sealed class Rule(string sourcePath, StringComparer identifierComparer) : IModuleRule
    {
        public List<CartesianJoinFinding> Findings { get; } = [];

        public void OnEnterQuerySpecificationScope(QuerySpecification node, ScopeChain scopeChain, ModuleWalker walker)
        {
            if (node.FromClause is { } from)
            {
                AnalyzeFromClause(from, node.WhereClause, scopeChain, walker);
            }
        }

        private void AnalyzeFromClause(FromClause from, WhereClause? whereClause, ScopeChain scopeChain, ModuleWalker walker)
        {
            var topLevel = from.TableReferences;

            ReportAlwaysFalseInnerJoinPredicates(topLevel, scopeChain, walker, sourcePath);
            ReportJoinPredicateEmptyWithWhereClause(topLevel, whereClause, scopeChain, walker, sourcePath);
            var allNamed = topLevel.SelectMany(PredicateTreeWalker.FlattenNamedTables).ToList();
            if (allNamed.Count < 2)
            {
                return;
            }

            var allJoinOnConditions = topLevel
                .SelectMany(PredicateTreeWalker.FlattenJoinNodes)
                .Select(j => j.SearchCondition)
                .ToList();

            var allPredicates = new List<BooleanExpression>(allJoinOnConditions);
            if (whereClause?.SearchCondition is { } where)
            {
                allPredicates.Add(where);
            }

            if (allPredicates.Any(p => FlattenLeaves(p).Any(HasUnqualifiedColumnReference)))
            {
                return;
            }

            var unionFind = BuildConnectivity(allNamed, allPredicates, identifierComparer);

            ReportExplicitCrossJoinGaps(topLevel, unionFind, sourcePath);

            ReportCommaJoinGap(topLevel, unionFind, sourcePath);
        }

        private static AliasUnionFind BuildConnectivity(
            List<NamedTableReference> allNamed, List<BooleanExpression> allPredicates, StringComparer identifierComparer)
        {
            var unionFind = new AliasUnionFind(allNamed.Select(AliasKey), identifierComparer);
            foreach (var leaf in allPredicates.SelectMany(FlattenLeaves))
            {
                var qualifiers = CollectQualifiers(leaf).Where(unionFind.Contains).ToList();
                for (var i = 1; i < qualifiers.Count; i++)
                {
                    unionFind.Union(qualifiers[0], qualifiers[i]);
                }
            }

            return unionFind;
        }

        private void ReportAlwaysFalseInnerJoinPredicates(
            IList<TableReference> topLevel, ScopeChain scopeChain, ModuleWalker walker, string sourcePath)
        {
            foreach (var join in topLevel.SelectMany(PredicateTreeWalker.FlattenJoinNodes))
            {
                if (join.QualifiedJoinType != QualifiedJoinType.Inner
                    || join.SearchCondition is not { } condition
                    || join.FirstTableReference is not NamedTableReference first
                    || join.SecondTableReference is not NamedTableReference second)
                {
                    continue;
                }

                if (PredicateSurvivalAnalyzer.IsUnsatisfiable(condition, columnRef => walker.ResolveColumnFacts(columnRef, scopeChain)))
                {
                    Findings.Add(new CartesianJoinFinding(
                        CartesianJoinKind.AlwaysFalseInnerJoinPredicate,
                        SchemaObjectNameHelper.Qualify(first.SchemaObject),
                        SchemaObjectNameHelper.Qualify(second.SchemaObject),
                        sourcePath, condition.StartLine, condition.StartColumn,
                        FindingConfidence.High));
                }
            }
        }

        private void ReportJoinPredicateEmptyWithWhereClause(
            IList<TableReference> topLevel, WhereClause? whereClause, ScopeChain scopeChain, ModuleWalker walker, string sourcePath)
        {
            foreach (var join in topLevel.SelectMany(PredicateTreeWalker.FlattenJoinNodes))
            {
                if (join.QualifiedJoinType != QualifiedJoinType.Inner
                    || join.SearchCondition is not { } condition
                    || join.FirstTableReference is not NamedTableReference first
                    || join.SecondTableReference is not NamedTableReference second
                    || PredicateSurvivalAnalyzer.IsUnsatisfiable(condition, columnRef => walker.ResolveColumnFacts(columnRef, scopeChain)))
                {
                    continue;
                }

                var onConjuncts = PredicateTreeWalker.FlattenAnd(condition).ToList();
                if (FindEquiJoinEdge(onConjuncts, AliasKey(first), AliasKey(second)) is not { } edge)
                {
                    continue;
                }

                var allConjuncts = onConjuncts.Concat(PredicateTreeWalker.FlattenAnd(whereClause?.SearchCondition)).ToList();
                var firstRange = BuildNumericRange(allConjuncts, edge.FirstQualifier, edge.FirstColumn);
                var secondRange = BuildNumericRange(allConjuncts, edge.SecondQualifier, edge.SecondColumn);

                if (!firstRange.Intersect(secondRange).IsEmpty)
                {
                    continue;
                }

                Findings.Add(new CartesianJoinFinding(
                    CartesianJoinKind.JoinPredicateEmptyWithWhereClause,
                    SchemaObjectNameHelper.Qualify(first.SchemaObject),
                    SchemaObjectNameHelper.Qualify(second.SchemaObject),
                    sourcePath, condition.StartLine, condition.StartColumn,
                    FindingConfidence.High));
            }
        }

        private readonly record struct EquiJoinEdge(string FirstQualifier, string FirstColumn, string SecondQualifier, string SecondColumn);

        private static EquiJoinEdge? FindEquiJoinEdge(IEnumerable<BooleanExpression> conjuncts, string firstAlias, string secondAlias)
        {
            foreach (var conjunct in conjuncts)
            {
                if (conjunct is not BooleanComparisonExpression { ComparisonType: BooleanComparisonType.Equals } cmp
                    || TryGetQualifiedColumn(cmp.FirstExpression) is not { } left
                    || TryGetQualifiedColumn(cmp.SecondExpression) is not { } right)
                {
                    continue;
                }

                if (string.Equals(left.Qualifier, firstAlias, StringComparison.OrdinalIgnoreCase)
                    && string.Equals(right.Qualifier, secondAlias, StringComparison.OrdinalIgnoreCase))
                {
                    return new EquiJoinEdge(left.Qualifier, left.Column, right.Qualifier, right.Column);
                }

                if (string.Equals(left.Qualifier, secondAlias, StringComparison.OrdinalIgnoreCase)
                    && string.Equals(right.Qualifier, firstAlias, StringComparison.OrdinalIgnoreCase))
                {
                    return new EquiJoinEdge(right.Qualifier, right.Column, left.Qualifier, left.Column);
                }
            }

            return null;
        }

        private static (string Qualifier, string Column)? TryGetQualifiedColumn(ScalarExpression expr) =>
            expr is ColumnReferenceExpression { MultiPartIdentifier.Identifiers: { Count: >= 2 } ids }
                ? (ids[^2].Value, ids[^1].Value)
                : null;

        private static NumericValueRangeSet BuildNumericRange(IEnumerable<BooleanExpression> conjuncts, string qualifier, string column)
        {
            var range = NumericValueRangeSet.Universal;
            foreach (var conjunct in conjuncts)
            {
                if (conjunct is not BooleanComparisonExpression cmp || CmpOpHelper.ToCmpOp(cmp.ComparisonType) is not { } op)
                {
                    continue;
                }

                if (TryGetQualifiedColumn(cmp.FirstExpression) is { } left && MatchesColumn(left, qualifier, column)
                    && TryGetNumericLiteral(cmp.SecondExpression) is { } rightValue)
                {
                    range = range.Intersect(ToRangeSet(op, rightValue));
                    continue;
                }

                if (TryGetQualifiedColumn(cmp.SecondExpression) is { } right && MatchesColumn(right, qualifier, column)
                    && TryGetNumericLiteral(cmp.FirstExpression) is { } leftValue)
                {
                    range = range.Intersect(ToRangeSet(CmpOpHelper.Flip(op), leftValue));
                }
            }

            return range;
        }

        private static bool MatchesColumn((string Qualifier, string Column) candidate, string qualifier, string column) =>
            string.Equals(candidate.Qualifier, qualifier, StringComparison.OrdinalIgnoreCase)
            && string.Equals(candidate.Column, column, StringComparison.OrdinalIgnoreCase);

        private static decimal? TryGetNumericLiteral(ScalarExpression expr) => expr switch
        {
            IntegerLiteral lit => ParseDecimal(lit.Value),
            NumericLiteral lit => ParseDecimal(lit.Value),
            MoneyLiteral lit => ParseDecimal(lit.Value),
            UnaryExpression { UnaryExpressionType: UnaryExpressionType.Negative } unary =>
                TryGetNumericLiteral(unary.Expression) is { } v ? -v : null,
            UnaryExpression { UnaryExpressionType: UnaryExpressionType.Positive } unary =>
                TryGetNumericLiteral(unary.Expression),
            _ => null,
        };

        private static decimal? ParseDecimal(string value) =>
            decimal.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : null;

        private static NumericValueRangeSet ToRangeSet(CmpOp op, decimal value) => op switch
        {
            CmpOp.Eq => NumericValueRangeSet.ForEquals(value),
            CmpOp.Ne => NumericValueRangeSet.ForNotEquals(value),
            CmpOp.Lt => NumericValueRangeSet.ForLessThan(value),
            CmpOp.Le => NumericValueRangeSet.ForLessThanOrEqual(value),
            CmpOp.Gt => NumericValueRangeSet.ForGreaterThan(value),
            CmpOp.Ge => NumericValueRangeSet.ForGreaterThanOrEqual(value),
            _ => NumericValueRangeSet.Universal,
        };

        private void ReportExplicitCrossJoinGaps(
            IList<TableReference> topLevel, AliasUnionFind unionFind, string sourcePath)
        {
            foreach (var top in topLevel)
            {
                foreach (var unqualified in PredicateTreeWalker.FlattenUnqualifiedJoins(top))
                {
                    if (unqualified.UnqualifiedJoinType != UnqualifiedJoinType.CrossJoin)
                    {
                        continue;
                    }

                    if (unqualified.FirstTableReference is NamedTableReference crossFirst
                        && unqualified.SecondTableReference is NamedTableReference crossSecond
                        && !unionFind.SameComponent(AliasKey(crossFirst), AliasKey(crossSecond)))
                    {
                        Findings.Add(new CartesianJoinFinding(
                            CartesianJoinKind.ExplicitCrossJoin,
                            SchemaObjectNameHelper.Qualify(crossFirst.SchemaObject),
                            SchemaObjectNameHelper.Qualify(crossSecond.SchemaObject),
                            sourcePath, crossSecond.StartLine, crossSecond.StartColumn,
                            FindingConfidence.Medium));
                    }
                }
            }
        }

        private void ReportCommaJoinGap(
            IList<TableReference> topLevel, AliasUnionFind unionFind, string sourcePath)
        {
            for (var i = 0; i < topLevel.Count; i++)
            {
                if (topLevel[i] is not NamedTableReference firstNamed)
                {
                    continue;
                }

                for (var j = i + 1; j < topLevel.Count; j++)
                {
                    if (topLevel[j] is not NamedTableReference secondNamed
                        || unionFind.SameComponent(AliasKey(firstNamed), AliasKey(secondNamed)))
                    {
                        continue;
                    }

                    Findings.Add(new CartesianJoinFinding(
                        CartesianJoinKind.CommaJoin,
                        SchemaObjectNameHelper.Qualify(firstNamed.SchemaObject),
                        SchemaObjectNameHelper.Qualify(secondNamed.SchemaObject),
                        sourcePath, firstNamed.StartLine, firstNamed.StartColumn));
                    return;
                }
            }
        }

        private static string AliasKey(NamedTableReference named) =>
            named.Alias?.Value ?? named.SchemaObject.BaseIdentifier.Value;

        private static bool HasUnqualifiedColumnReference(BooleanExpression leaf) =>

            CollectColumnReferences(leaf).Any(c => c.MultiPartIdentifier is not { Identifiers.Count: >= 2 });

        private static HashSet<string> CollectQualifiers(BooleanExpression leaf) =>
            [.. CollectColumnReferences(leaf)
                .Where(c => c.MultiPartIdentifier is { Identifiers.Count: >= 2 })
                .Select(c => c.MultiPartIdentifier.Identifiers[^2].Value)];

        private static List<ColumnReferenceExpression> CollectColumnReferences(TSqlFragment fragment)
        {
            var collector = new ColumnAliasHelpers.RawColumnReferenceCollector();
            fragment.Accept(collector);
            return collector.References;
        }

        private static IEnumerable<BooleanExpression> FlattenLeaves(BooleanExpression? expression)
        {
            switch (expression)
            {
                case null:
                    yield break;

                case BooleanBinaryExpression binary:
                    foreach (var e in FlattenLeaves(binary.FirstExpression))
                    {
                        yield return e;
                    }

                    foreach (var e in FlattenLeaves(binary.SecondExpression))
                    {
                        yield return e;
                    }

                    break;

                case BooleanParenthesisExpression paren:
                    foreach (var e in FlattenLeaves(paren.Expression))
                    {
                        yield return e;
                    }

                    break;

                case BooleanNotExpression not:
                    foreach (var e in FlattenLeaves(not.Expression))
                    {
                        yield return e;
                    }

                    break;

                default:
                    yield return expression;
                    break;
            }
        }

        private sealed class AliasUnionFind
        {
            private readonly Dictionary<string, string> _parent;

            private readonly StringComparer _identifierComparer;

            public AliasUnionFind(IEnumerable<string> aliases, StringComparer identifierComparer)
            {
                _identifierComparer = identifierComparer;
                _parent = new Dictionary<string, string>(identifierComparer);
                foreach (var alias in aliases)
                {
                    _parent.TryAdd(alias, alias);
                }
            }

            public bool Contains(string alias) => _parent.ContainsKey(alias);

            public void Union(string a, string b)
            {
                var rootA = Find(a);
                var rootB = Find(b);
                if (!_identifierComparer.Equals(rootA, rootB))
                {
                    _parent[rootA] = rootB;
                }
            }

            public bool SameComponent(string a, string b) =>
                _identifierComparer.Equals(Find(a), Find(b));

            private string Find(string alias)
            {
                while (!_identifierComparer.Equals(_parent[alias], alias))
                {
                    _parent[alias] = _parent[_parent[alias]];
                    alias = _parent[alias];
                }

                return alias;
            }
        }

    }
}
