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
        private static readonly IReadOnlyDictionary<string, ResolvedRelation> EmptyResolvedViews = new Dictionary<string, ResolvedRelation>();

        private readonly Stack<IReadOnlyDictionary<string, ResolvedRelation>> cteScopeStack = new();

        public List<FloatEqualityFinding> Findings { get; } = [];

        public override void ExplicitVisit(SelectStatement node)
        {
            cteScopeStack.Push(CteResolver.Resolve(node.WithCtesAndXmlNamespaces, catalog, EmptyResolvedViews, sourcePath, ledger: null));
            base.ExplicitVisit(node);
            cteScopeStack.Pop();
        }

        public override void ExplicitVisit(QuerySpecification node)
        {
            var cteRelations = cteScopeStack.Count > 0 ? cteScopeStack.Peek() : EmptyResolvedViews;
            var scopeChain = ScopeChainOf(FromScopeResolver.Resolve(node.FromClause, ResolutionContext(cteRelations)));

            if (node.WhereClause?.SearchCondition is { } whereCondition)
            {
                Inspect(whereCondition, scopeChain);
            }

            InspectJoinOnClauses(node.FromClause?.TableReferences, scopeChain);

            base.ExplicitVisit(node);
        }

        public override void ExplicitVisit(UpdateStatement node)
        {
            var spec = node.UpdateSpecification;
            var cteRelations = CteResolver.Resolve(node.WithCtesAndXmlNamespaces, catalog, EmptyResolvedViews, sourcePath, ledger: null);
            var scopeChain = ScopeChainOf(FromScopeResolver.ResolveForDataModification(spec.Target, spec.FromClause, ResolutionContext(cteRelations)));

            if (spec.WhereClause?.SearchCondition is { } whereCondition)
            {
                Inspect(whereCondition, scopeChain);
            }

            InspectJoinOnClauses(spec.FromClause?.TableReferences, scopeChain);

            base.ExplicitVisit(node);
        }

        public override void ExplicitVisit(DeleteStatement node)
        {
            var spec = node.DeleteSpecification;
            var cteRelations = CteResolver.Resolve(node.WithCtesAndXmlNamespaces, catalog, EmptyResolvedViews, sourcePath, ledger: null);
            var scopeChain = ScopeChainOf(FromScopeResolver.ResolveForDataModification(spec.Target, spec.FromClause, ResolutionContext(cteRelations)));

            if (spec.WhereClause?.SearchCondition is { } whereCondition)
            {
                Inspect(whereCondition, scopeChain);
            }

            InspectJoinOnClauses(spec.FromClause?.TableReferences, scopeChain);

            base.ExplicitVisit(node);
        }

        private FromScopeResolver.ResolutionContext ResolutionContext(IReadOnlyDictionary<string, ResolvedRelation> cteRelations) =>
            new(catalog, EmptyResolvedViews, sourcePath, Ledger: null, cteRelations, ProcScope: null);

        private static List<(IReadOnlyDictionary<string, ScopeEntry> ByAlias, IReadOnlyList<ScopeEntry> Ordered)> ScopeChainOf(
            (IReadOnlyDictionary<string, ScopeEntry> ByAlias, IReadOnlyList<ScopeEntry> Ordered) resolved) => [resolved];

        private void InspectJoinOnClauses(
            IList<TableReference>? tableReferences,
            IReadOnlyList<(IReadOnlyDictionary<string, ScopeEntry> ByAlias, IReadOnlyList<ScopeEntry> Ordered)> scopeChain)
        {
            if (tableReferences is null)
            {
                return;
            }

            foreach (var reference in tableReferences)
            {
                foreach (var join in PredicateTreeWalker.FlattenJoinNodes(reference).Where(j => j.SearchCondition is not null))
                {
                    Inspect(join.SearchCondition!, scopeChain);
                }
            }
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

            }
        }
    }
}
