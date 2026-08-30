using Microsoft.SqlServer.TransactSql.ScriptDom;
using SilentScan.Core.Catalog;
using SilentScan.Core.Lineage;
using SilentScan.Core.Parsing;
using SilentScan.Core.TypeInference;

namespace SilentScan.Core.Predicates;

public static class FloatEqualityPredicateScanner
{
    private static readonly IReadOnlyDictionary<string, ResolvedRelation> EmptyResolvedViews = new Dictionary<string, ResolvedRelation>();

    public static IReadOnlyList<FloatEqualityFinding> Scan(SqlParseResult parseResult, DatabaseCatalog catalog)
    {
        var visitor = new Visitor(parseResult.SourcePath, catalog);
        parseResult.Fragment.Accept(visitor);
        return
        [
            .. visitor.Findings
                .OrderBy(f => f.SourcePath, StringComparer.Ordinal)
                .ThenBy(f => f.Line)
                .ThenBy(f => f.Column),
        ];
    }

#pragma warning disable CS9107
    private sealed class Visitor(string sourcePath, DatabaseCatalog catalog)
        : ScopedSqlVisitorBase(sourcePath, catalog, EmptyResolvedViews, ledger: null, currentProcScope: null, callerScopeByCalleeScope: null)
#pragma warning restore CS9107
    {
        public List<FloatEqualityFinding> Findings { get; } = [];

        protected override void OnQuerySpecificationScope(QuerySpecification node, ScopeChain scopeChain, Action continueDescent)
        {
            if (node.WhereClause?.SearchCondition is { } whereCondition)
            {
                Inspect(whereCondition, scopeChain);
            }

            if (node.HavingClause?.SearchCondition is { } havingCondition)
            {
                Inspect(havingCondition, scopeChain);
            }

            InspectJoinOnClauses(node.FromClause?.TableReferences, scopeChain, Inspect);
            continueDescent();
        }

        protected override void OnUpdateStatementScope(UpdateStatement node, ScopeChain scopeChain, Action continueDescent)
        {
            var spec = node.UpdateSpecification;
            if (spec.WhereClause?.SearchCondition is { } whereCondition)
            {
                Inspect(whereCondition, scopeChain);
            }

            InspectJoinOnClauses(spec.FromClause?.TableReferences, scopeChain, Inspect);
            continueDescent();
        }

        protected override void OnDeleteStatementScope(DeleteStatement node, ScopeChain scopeChain, Action continueDescent)
        {
            var spec = node.DeleteSpecification;
            if (spec.WhereClause?.SearchCondition is { } whereCondition)
            {
                Inspect(whereCondition, scopeChain);
            }

            InspectJoinOnClauses(spec.FromClause?.TableReferences, scopeChain, Inspect);
            continueDescent();
        }

        private void Inspect(BooleanExpression searchCondition, ScopeChain scopeChain)
        {
            var collector = new EqualityCollector();
            searchCondition.Accept(collector);
            foreach (var comparison in collector.Comparisons)
            {
                InspectEquality(comparison, scopeChain);
            }
        }

        private void InspectEquality(BooleanComparisonExpression comparison, ScopeChain scopeChain)
        {
            foreach (var side in new[] { comparison.FirstExpression, comparison.SecondExpression })
            {
                if (side is not ColumnReferenceExpression columnRef
                    || BaseColumnResolver.ResolveBaseColumn(columnRef, sourcePath, scopeChain, catalog) is not { } resolved
                    || resolved.Type?.Category is not (SqlTypeCategory.Real or SqlTypeCategory.Float))
                {
                    continue;
                }

                Findings.Add(new FloatEqualityFinding(
                    resolved.TableQualifiedName,
                    resolved.ColumnName,
                    resolved.Type!.ToString(),
                    sourcePath,
                    comparison.StartLine,
                    comparison.StartColumn));

                return;
            }
        }

        private sealed class EqualityCollector : TSqlFragmentVisitor
        {
            public List<BooleanComparisonExpression> Comparisons { get; } = [];

            public override void ExplicitVisit(BooleanComparisonExpression node)
            {
                if (node.ComparisonType == BooleanComparisonType.Equals)
                {
                    Comparisons.Add(node);
                }

                base.ExplicitVisit(node);
            }

            public override void ExplicitVisit(QuerySpecification node)
            {
                _ = node;
            }
        }
    }
}
