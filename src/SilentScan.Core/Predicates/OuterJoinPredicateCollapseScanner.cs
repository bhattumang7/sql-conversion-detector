using Microsoft.SqlServer.TransactSql.ScriptDom;
using SilentScan.Core.Catalog;
using SilentScan.Core.Lineage;
using SilentScan.Core.Parsing;
using SilentScan.Core.Common;

namespace SilentScan.Core.Predicates;

public static class OuterJoinPredicateCollapseScanner
{
    public static IReadOnlyList<OuterJoinPredicateCollapseFinding> Scan(SqlParseResult parseResult, DatabaseCatalog? catalog = null)
    {
        var rule = CreateRule(parseResult.SourcePath, catalog);
        var walker = new ModuleWalker(parseResult.SourcePath, catalog ?? new DatabaseCatalog(), EmptyResolvedViews, rules: [rule]);
        parseResult.Fragment.Accept(walker);
        return Harvest(rule);
    }

    internal static Rule CreateRule(string sourcePath, DatabaseCatalog? catalog = null) => new(sourcePath, catalog?.IdentifierComparer ?? StringComparer.OrdinalIgnoreCase);

    internal static IReadOnlyList<OuterJoinPredicateCollapseFinding> Harvest(Rule rule) =>
        [
            .. rule.Findings
                .OrderBy(f => f.SourcePath, StringComparer.Ordinal)
                .ThenBy(f => f.Line)
                .ThenBy(f => f.Column),
        ];

    private static readonly IReadOnlyDictionary<string, ResolvedRelation> EmptyResolvedViews = new Dictionary<string, ResolvedRelation>();

    internal sealed class Rule(string sourcePath, StringComparer identifierComparer) : IModuleRule
    {
        public List<OuterJoinPredicateCollapseFinding> Findings { get; } = [];

        public void OnEnterQuerySpecificationScope(QuerySpecification node, ScopeChain scopeChain, ModuleWalker walker) =>
            Inspect(node.FromClause, node.WhereClause);

        public void OnEnterUpdateStatementScope(UpdateStatement node, ScopeChain scopeChain, ModuleWalker walker) =>
            Inspect(node.UpdateSpecification.FromClause, node.UpdateSpecification.WhereClause);

        public void OnEnterDeleteStatementScope(DeleteStatement node, ScopeChain scopeChain, ModuleWalker walker) =>
            Inspect(node.DeleteSpecification.FromClause, node.DeleteSpecification.WhereClause);

        private void Inspect(FromClause? fromClause, WhereClause? whereClause)
        {
            if (fromClause is null || whereClause?.SearchCondition is not { } searchCondition)
            {
                return;
            }

            var nullSupplyingAliases = CollectNullSupplyingAliases(fromClause, identifierComparer);
            if (nullSupplyingAliases.Count == 0)
            {
                return;
            }

            foreach (var conjunct in PredicateTreeWalker.FlattenAnd(searchCondition))
            {
                InspectConjunct(conjunct, nullSupplyingAliases);
            }
        }

        private void InspectConjunct(BooleanExpression conjunct, Dictionary<string, AliasNullSide> nullSupplyingAliases)
        {
            var unwrapped = conjunct;
            while (unwrapped is BooleanParenthesisExpression paren)
            {
                unwrapped = paren.Expression;
            }

            switch (unwrapped)
            {
                case BooleanBinaryExpression { BinaryExpressionType: BooleanBinaryExpressionType.Or }:
                case BooleanNotExpression:
                    return;

                case BooleanComparisonExpression cmp:
                    InspectOperand(cmp.FirstExpression, nullSupplyingAliases);
                    InspectOperand(cmp.SecondExpression, nullSupplyingAliases);
                    break;

                case BooleanTernaryExpression between:
                    InspectOperand(between.FirstExpression, nullSupplyingAliases);
                    break;

                case InPredicate inPredicate:
                    InspectOperand(inPredicate.Expression, nullSupplyingAliases);
                    break;

                case LikePredicate like:
                    InspectOperand(like.FirstExpression, nullSupplyingAliases);
                    break;
            }
        }

        private void InspectOperand(ScalarExpression operand, Dictionary<string, AliasNullSide> nullSupplyingAliases)
        {
            if (operand is not ColumnReferenceExpression { MultiPartIdentifier.Identifiers: { Count: >= 2 } ids } columnRef)
            {
                return;
            }

            var qualifier = ids[^2].Value;
            if (!nullSupplyingAliases.TryGetValue(qualifier, out var side))
            {
                return;
            }

            Findings.Add(new OuterJoinPredicateCollapseFinding(
                side.Kind,
                side.TableQualifiedName,
                ids[^1].Value,
                sourcePath,
                columnRef.StartLine,
                columnRef.StartColumn));
        }

        private static Dictionary<string, AliasNullSide> CollectNullSupplyingAliases(FromClause fromClause, StringComparer identifierComparer)
        {
            var result = new Dictionary<string, AliasNullSide>(identifierComparer);
            foreach (var top in fromClause.TableReferences)
            {
                foreach (var join in PredicateTreeWalker.FlattenJoinNodes(top))
                {
                    switch (join.QualifiedJoinType)
                    {
                        case QualifiedJoinType.LeftOuter:
                            AddAliases(join.SecondTableReference, OuterJoinPredicateCollapseKind.LeftOuterJoin, result);
                            break;

                        case QualifiedJoinType.RightOuter:
                            AddAliases(join.FirstTableReference, OuterJoinPredicateCollapseKind.RightOuterJoin, result);
                            break;

                        case QualifiedJoinType.FullOuter:
                            AddAliases(join.FirstTableReference, OuterJoinPredicateCollapseKind.FullOuterJoin, result);
                            AddAliases(join.SecondTableReference, OuterJoinPredicateCollapseKind.FullOuterJoin, result);
                            break;
                    }
                }
            }

            return result;
        }

        private static void AddAliases(
            TableReference branch, OuterJoinPredicateCollapseKind kind, Dictionary<string, AliasNullSide> result)
        {
            foreach (var named in PredicateTreeWalker.FlattenNamedTables(branch))
            {
                var alias = AliasKey(named);
                if (!result.ContainsKey(alias))
                {
                    result.Add(alias, new AliasNullSide(kind, SchemaObjectNameHelper.Qualify(named.SchemaObject)));
                }
            }
        }

        private static string AliasKey(NamedTableReference named) =>
            named.Alias?.Value ?? named.SchemaObject.BaseIdentifier.Value;

        private readonly record struct AliasNullSide(OuterJoinPredicateCollapseKind Kind, string TableQualifiedName);
    }
}
