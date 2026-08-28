using Microsoft.SqlServer.TransactSql.ScriptDom;
using SilentScan.Core.Catalog;
using SilentScan.Core.Lineage;
using SilentScan.Core.Parsing;
using SilentScan.Core.Predicates.Normalization;

namespace SilentScan.Core.Predicates;

public static class NotInNullableSubqueryScanner
{
    private static readonly IReadOnlyDictionary<string, ResolvedRelation> EmptyResolvedViews = new Dictionary<string, ResolvedRelation>();

    public static IReadOnlyList<NotInNullableSubqueryFinding> Scan(SqlParseResult parseResult, DatabaseCatalog catalog)
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
        public List<NotInNullableSubqueryFinding> Findings { get; } = [];

        public override void ExplicitVisit(SelectStatement node)
        {
            PushCteScope(node.WithCtesAndXmlNamespaces);
            base.ExplicitVisit(node);
            PopCteScope();
        }

        public override void ExplicitVisit(QuerySpecification node)
        {
            var (byAlias, ordered) = FromScopeResolver.Resolve(node.FromClause, CurrentResolutionContext());
            InspectSearchCondition(node.WhereClause?.SearchCondition, byAlias, ordered);
            base.ExplicitVisit(node);
        }

        public override void ExplicitVisit(UpdateStatement node)
        {
            PushCteScope(node.WithCtesAndXmlNamespaces);
            var spec = node.UpdateSpecification;
            var (byAlias, ordered) = FromScopeResolver.ResolveForDataModification(spec.Target, spec.FromClause, CurrentResolutionContext());
            InspectSearchCondition(spec.WhereClause?.SearchCondition, byAlias, ordered);
            base.ExplicitVisit(node);
            PopCteScope();
        }

        public override void ExplicitVisit(DeleteStatement node)
        {
            PushCteScope(node.WithCtesAndXmlNamespaces);
            var spec = node.DeleteSpecification;
            var (byAlias, ordered) = FromScopeResolver.ResolveForDataModification(spec.Target, spec.FromClause, CurrentResolutionContext());
            InspectSearchCondition(spec.WhereClause?.SearchCondition, byAlias, ordered);
            base.ExplicitVisit(node);
            PopCteScope();
        }

        private void InspectSearchCondition(
            BooleanExpression? searchCondition, IReadOnlyDictionary<string, ScopeEntry> byAlias, IReadOnlyList<ScopeEntry> ordered)
        {
            if (searchCondition is null)
            {
                return;
            }

            var scopeChain = new List<(IReadOnlyDictionary<string, ScopeEntry> ByAlias, IReadOnlyList<ScopeEntry> Ordered)> { (byAlias, ordered) };
            if (PredicateSurvivalAnalyzer.IsUnsatisfiable(searchCondition, columnRef => ResolveColumnFacts(columnRef, scopeChain)))
            {
                return;
            }

            foreach (var predicate in PredicateTreeWalker.FlattenAnd(searchCondition).OfType<InPredicate>())
            {
                TryMatch(predicate);
            }
        }

        private void TryMatch(InPredicate predicate)
        {
            if (!predicate.NotDefined || predicate.Subquery is not { QueryExpression: QuerySpecification subquerySpec })
            {
                return;
            }

            if (subquerySpec.SelectElements.Count != 1
                || subquerySpec.SelectElements[0] is not SelectScalarExpression { Expression: ColumnReferenceExpression innerColumnRef })
            {
                return;
            }

            var (innerByAlias, innerOrdered) = FromScopeResolver.Resolve(subquerySpec.FromClause, CurrentResolutionContext());
            var innerProvenance = ScalarExpressionResolver.ResolveColumnReference(innerColumnRef, [(innerByAlias, innerOrdered)], sourcePath, ledger: null, catalog);
            if (innerProvenance is not ColumnProvenance.BaseColumn { Depth: 0 } innerColumn)
            {
                return;
            }

            var catalogColumn = catalog.Find(innerColumn.TableQualifiedName)?.FindColumn(innerColumn.ColumnName, catalog.IdentifierComparer);
            if (catalogColumn is null || !catalogColumn.IsNullable)
            {
                return;
            }

            if (HasDefensiveNotNullFilter(subquerySpec.WhereClause?.SearchCondition, innerColumn.TableQualifiedName, innerColumn.ColumnName, innerByAlias, innerOrdered))
            {
                return;
            }

            var outerColumnName = predicate.Expression is ColumnReferenceExpression outerColumnRef
                ? outerColumnRef.MultiPartIdentifier.Identifiers[^1].Value
                : null;

            var indexed = catalog.Find(innerColumn.TableQualifiedName)?.IsIndexedColumn(innerColumn.ColumnName, catalog.IdentifierComparer) ?? false;

            Findings.Add(new NotInNullableSubqueryFinding(
                outerColumnName, innerColumn.TableQualifiedName, innerColumn.ColumnName, indexed,
                sourcePath, predicate.StartLine, predicate.StartColumn));
        }

        private bool HasDefensiveNotNullFilter(
            BooleanExpression? subqueryWhere, string tableQualifiedName, string columnName,
            IReadOnlyDictionary<string, ScopeEntry> innerByAlias, IReadOnlyList<ScopeEntry> innerOrdered)
        {
            foreach (var clause in PredicateTreeWalker.FlattenAnd(subqueryWhere))
            {
                if (clause is not BooleanIsNullExpression { IsNot: true, Expression: ColumnReferenceExpression filterColumnRef })
                {
                    continue;
                }

                var provenance = ScalarExpressionResolver.ResolveColumnReference(filterColumnRef, [(innerByAlias, innerOrdered)], sourcePath, ledger: null, catalog);
                if (provenance is ColumnProvenance.BaseColumn { Depth: 0 } filterColumn
                    && catalog.IdentifierComparer.Equals(filterColumn.TableQualifiedName, tableQualifiedName)
                    && catalog.IdentifierComparer.Equals(filterColumn.ColumnName, columnName))
                {
                    return true;
                }
            }

            return false;
        }
    }
}
