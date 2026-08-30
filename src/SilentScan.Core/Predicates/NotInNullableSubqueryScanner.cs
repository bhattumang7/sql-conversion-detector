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
            ScopeStack.Push(FromScopeResolver.Resolve(node.FromClause, CurrentResolutionContext()));
            InspectSearchCondition(node.WhereClause?.SearchCondition);
            base.ExplicitVisit(node);
            ScopeStack.Pop();
        }

        public override void ExplicitVisit(UpdateStatement node)
        {
            PushCteScope(node.WithCtesAndXmlNamespaces);
            var spec = node.UpdateSpecification;
            ScopeStack.Push(FromScopeResolver.ResolveForDataModification(spec.Target, spec.FromClause, CurrentResolutionContext()));
            InspectSearchCondition(spec.WhereClause?.SearchCondition);
            base.ExplicitVisit(node);
            ScopeStack.Pop();
            PopCteScope();
        }

        public override void ExplicitVisit(DeleteStatement node)
        {
            PushCteScope(node.WithCtesAndXmlNamespaces);
            var spec = node.DeleteSpecification;
            ScopeStack.Push(FromScopeResolver.ResolveForDataModification(spec.Target, spec.FromClause, CurrentResolutionContext()));
            InspectSearchCondition(spec.WhereClause?.SearchCondition);
            base.ExplicitVisit(node);
            ScopeStack.Pop();
            PopCteScope();
        }

        private void InspectSearchCondition(BooleanExpression? searchCondition)
        {
            if (searchCondition is null)
            {
                return;
            }

            var scopeChain = CurrentScopeChain();
            if (PredicateSurvivalAnalyzer.IsUnsatisfiable(searchCondition, columnRef => ResolveColumnFacts(columnRef, scopeChain)))
            {
                return;
            }

            foreach (var predicate in PredicateTreeWalker.FlattenAnd(searchCondition).OfType<InPredicate>())
            {
                TryMatch(predicate, scopeChain);
            }
        }

        private void TryMatch(
            InPredicate predicate,
            IReadOnlyList<(IReadOnlyDictionary<string, ScopeEntry> ByAlias, IReadOnlyList<ScopeEntry> Ordered)> enclosingScopeChain)
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

            var innerScope = FromScopeResolver.Resolve(subquerySpec.FromClause, CurrentResolutionContext());
            var subqueryScopeChain = new List<(IReadOnlyDictionary<string, ScopeEntry> ByAlias, IReadOnlyList<ScopeEntry> Ordered)> { innerScope };
            subqueryScopeChain.AddRange(enclosingScopeChain);

            var innerProvenance = ScalarExpressionResolver.ResolveColumnReference(innerColumnRef, subqueryScopeChain, sourcePath, ledger: null, catalog);
            if (innerProvenance is not ColumnProvenance.BaseColumn { Depth: 0 } innerColumn)
            {
                return;
            }

            var catalogColumn = catalog.Find(innerColumn.TableQualifiedName)?.FindColumn(innerColumn.ColumnName, catalog.IdentifierComparer);
            if (catalogColumn is null || !catalogColumn.IsNullable)
            {
                return;
            }

            if (HasDefensiveNotNullFilter(subquerySpec.WhereClause?.SearchCondition, innerColumn.TableQualifiedName, innerColumn.ColumnName, subqueryScopeChain))
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
            IReadOnlyList<(IReadOnlyDictionary<string, ScopeEntry> ByAlias, IReadOnlyList<ScopeEntry> Ordered)> subqueryScopeChain)
        {
            foreach (var clause in PredicateTreeWalker.FlattenAnd(subqueryWhere))
            {
                if (clause is not BooleanIsNullExpression { IsNot: true, Expression: ColumnReferenceExpression filterColumnRef })
                {
                    continue;
                }

                var provenance = ScalarExpressionResolver.ResolveColumnReference(filterColumnRef, subqueryScopeChain, sourcePath, ledger: null, catalog);
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
