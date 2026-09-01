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
