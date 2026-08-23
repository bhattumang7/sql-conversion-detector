using Microsoft.SqlServer.TransactSql.ScriptDom;
using SilentScan.Core.Catalog;
using SilentScan.Core.Lineage;
using SilentScan.Core.Parsing;
using SilentScan.Core.TypeInference;

namespace SilentScan.Core.Predicates;

public static class FloatEqualityPredicateScanner
{
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

    private sealed class Visitor(string sourcePath, DatabaseCatalog catalog) : TSqlFragmentVisitor
    {
        private readonly CteScopeTracker cteScope = new(sourcePath, catalog);

        public List<FloatEqualityFinding> Findings { get; } = [];

        public override void ExplicitVisit(SelectStatement node)
        {
            cteScope.PushForSelect(node.WithCtesAndXmlNamespaces);
            base.ExplicitVisit(node);
            cteScope.Pop();
        }

        public override void ExplicitVisit(QuerySpecification node)
        {
            var scopeChain = PredicateVisitorSupport.ScopeChainOf(FromScopeResolver.Resolve(node.FromClause, PredicateVisitorSupport.ResolutionContext(cteScope.Current, sourcePath, catalog)));

            if (node.WhereClause?.SearchCondition is { } whereCondition)
            {
                Inspect(whereCondition, scopeChain);
            }

            PredicateVisitorSupport.InspectJoinOnClauses(node.FromClause?.TableReferences, scopeChain, Inspect);

            base.ExplicitVisit(node);
        }

        public override void ExplicitVisit(UpdateStatement node)
        {
            var spec = node.UpdateSpecification;
            var cteRelations = CteResolver.Resolve(node.WithCtesAndXmlNamespaces, catalog, PredicateVisitorSupport.EmptyResolvedViews, sourcePath, ledger: null);
            var scopeChain = PredicateVisitorSupport.ScopeChainOf(FromScopeResolver.ResolveForDataModification(spec.Target, spec.FromClause, PredicateVisitorSupport.ResolutionContext(cteRelations, sourcePath, catalog)));

            if (spec.WhereClause?.SearchCondition is { } whereCondition)
            {
                Inspect(whereCondition, scopeChain);
            }

            PredicateVisitorSupport.InspectJoinOnClauses(spec.FromClause?.TableReferences, scopeChain, Inspect);

            base.ExplicitVisit(node);
        }

        public override void ExplicitVisit(DeleteStatement node)
        {
            var spec = node.DeleteSpecification;
            var cteRelations = CteResolver.Resolve(node.WithCtesAndXmlNamespaces, catalog, PredicateVisitorSupport.EmptyResolvedViews, sourcePath, ledger: null);
            var scopeChain = PredicateVisitorSupport.ScopeChainOf(FromScopeResolver.ResolveForDataModification(spec.Target, spec.FromClause, PredicateVisitorSupport.ResolutionContext(cteRelations, sourcePath, catalog)));

            if (spec.WhereClause?.SearchCondition is { } whereCondition)
            {
                Inspect(whereCondition, scopeChain);
            }

            PredicateVisitorSupport.InspectJoinOnClauses(spec.FromClause?.TableReferences, scopeChain, Inspect);

            base.ExplicitVisit(node);
        }

        private void Inspect(
            BooleanExpression searchCondition,
            IReadOnlyList<(IReadOnlyDictionary<string, ScopeEntry> ByAlias, IReadOnlyList<ScopeEntry> Ordered)> scopeChain)
        {
            var collector = new EqualityCollector();
            searchCondition.Accept(collector);
            foreach (var comparison in collector.Comparisons)
            {
                InspectEquality(comparison, scopeChain);
            }
        }

        private void InspectEquality(
            BooleanComparisonExpression comparison,
            IReadOnlyList<(IReadOnlyDictionary<string, ScopeEntry> ByAlias, IReadOnlyList<ScopeEntry> Ordered)> scopeChain)
        {
            foreach (var side in new[] { comparison.FirstExpression, comparison.SecondExpression })
            {
                if (side is not ColumnReferenceExpression columnRef
                    || BaseColumnResolver.ResolveBaseColumn(columnRef, sourcePath, scopeChain) is not { } resolved
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
