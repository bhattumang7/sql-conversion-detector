using Microsoft.SqlServer.TransactSql.ScriptDom;
using SilentScan.Core.Catalog;
using SilentScan.Core.Parsing;
using SilentScan.Core.Common;

namespace SilentScan.Core.Predicates;

public static class CartesianJoinScanner
{
    public static IReadOnlyList<CartesianJoinFinding> Scan(SqlParseResult parseResult)
    {
        var visitor = new Visitor(parseResult.SourcePath);
        parseResult.Fragment.Accept(visitor);
        return
        [
            .. visitor.Findings
                .OrderBy(f => f.SourcePath, StringComparer.Ordinal)
                .ThenBy(f => f.Line)
                .ThenBy(f => f.Column),
        ];
    }

    private sealed class Visitor(string sourcePath) : TSqlFragmentVisitor
    {
        public List<CartesianJoinFinding> Findings { get; } = [];

        public override void ExplicitVisit(QuerySpecification node)
        {
            if (node.FromClause is { } from)
            {
                AnalyzeFromClause(from, node.WhereClause);
            }

            base.ExplicitVisit(node);
        }

        private void AnalyzeFromClause(FromClause from, WhereClause? whereClause)
        {
            var topLevel = from.TableReferences;
            var allNamed = topLevel.SelectMany(PredicateTreeWalker.FlattenNamedTables).ToList();
            if (allNamed.Count < 2)
            {
                return;
            }

            var allJoinOnConditions = topLevel
                .SelectMany(FlattenJoinNodes)
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

            var unionFind = BuildConnectivity(allNamed, allPredicates);

            ReportExplicitCrossJoinGaps(topLevel, unionFind, sourcePath);

            ReportCommaJoinGap(topLevel, unionFind, sourcePath);
        }

        private static AliasUnionFind BuildConnectivity(
            List<NamedTableReference> allNamed, List<BooleanExpression> allPredicates)
        {
            var unionFind = new AliasUnionFind(allNamed.Select(AliasKey));
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

        private void ReportExplicitCrossJoinGaps(
            IList<TableReference> topLevel, AliasUnionFind unionFind, string sourcePath)
        {
            foreach (var top in topLevel)
            {
                foreach (var unqualified in FlattenUnqualifiedJoins(top))
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

private static IEnumerable<QualifiedJoin> FlattenJoinNodes(TableReference tableReference)
        {
            switch (tableReference)
            {
                case QualifiedJoin join:
                    foreach (var t in FlattenJoinNodes(join.FirstTableReference))
                    {
                        yield return t;
                    }

                    foreach (var t in FlattenJoinNodes(join.SecondTableReference))
                    {
                        yield return t;
                    }

                    yield return join;
                    break;

                case JoinParenthesisTableReference parenthesis:
                    foreach (var t in FlattenJoinNodes(parenthesis.Join))
                    {
                        yield return t;
                    }

                    break;
            }
        }

private static IEnumerable<UnqualifiedJoin> FlattenUnqualifiedJoins(TableReference tableReference)
        {
            switch (tableReference)
            {
                case UnqualifiedJoin join:
                    foreach (var t in FlattenUnqualifiedJoins(join.FirstTableReference))
                    {
                        yield return t;
                    }

                    foreach (var t in FlattenUnqualifiedJoins(join.SecondTableReference))
                    {
                        yield return t;
                    }

                    yield return join;
                    break;

                case QualifiedJoin qualified:
                    foreach (var t in FlattenUnqualifiedJoins(qualified.FirstTableReference))
                    {
                        yield return t;
                    }

                    foreach (var t in FlattenUnqualifiedJoins(qualified.SecondTableReference))
                    {
                        yield return t;
                    }

                    break;

                case JoinParenthesisTableReference parenthesis:
                    foreach (var t in FlattenUnqualifiedJoins(parenthesis.Join))
                    {
                        yield return t;
                    }

                    break;
            }
        }

private sealed class AliasUnionFind
        {
            private readonly Dictionary<string, string> _parent = new(StringComparer.OrdinalIgnoreCase);

            public AliasUnionFind(IEnumerable<string> aliases)
            {
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
                if (!string.Equals(rootA, rootB, StringComparison.OrdinalIgnoreCase))
                {
                    _parent[rootA] = rootB;
                }
            }

            public bool SameComponent(string a, string b) =>
                string.Equals(Find(a), Find(b), StringComparison.OrdinalIgnoreCase);

            private string Find(string alias)
            {
                while (!string.Equals(_parent[alias], alias, StringComparison.OrdinalIgnoreCase))
                {
                    _parent[alias] = _parent[_parent[alias]];
                    alias = _parent[alias];
                }

                return alias;
            }
        }

    }
}
