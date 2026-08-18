using Microsoft.SqlServer.TransactSql.ScriptDom;
using SilentScan.Core.Catalog;
using SilentScan.Core.Lineage;
using SilentScan.Core.Parsing;

namespace SilentScan.Core.Predicates;

/// <summary>
/// docs/detection-checklist.md Tier 2 "NOT IN over a nullable subquery column" - a standalone
/// scanner, not folded into <see cref="TypedPredicateExtractor"/>: <c>InPredicate</c> with a
/// <c>Subquery</c> is a materially different AST shape than a plain
/// <c>BooleanComparisonExpression</c>, and resolving the subquery's own projected column needs a
/// SECOND, independent <see cref="FromScopeResolver"/> call over the subquery's own
/// <c>FromClause</c> - a kind of nested-scope resolution none of the existing predicate scanners
/// do. <see cref="TypedPredicateExtractor"/> itself explicitly bails on <c>NOT IN</c> without
/// looking at the subquery at all (it is not sargable regardless of type match), so there is no
/// overlap or double-count risk between the two.
///
/// Deliberately base-table-only, Depth-0-only on the subquery side, like
/// <see cref="CatchAllPredicateScanner"/>/<see cref="PartialCompositeForeignKeyJoinScanner"/>:
/// only a bare <c>ColumnReferenceExpression</c> projected as the subquery's sole SELECT element,
/// resolving to a base table, is matched - an expression, a multi-column/<c>SELECT *</c>
/// subquery, or a set-operator (<c>UNION</c>/<c>EXCEPT</c>/<c>INTERSECT</c>) subquery is left
/// unanalyzed rather than guessed at.
/// </summary>
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

    private sealed class Visitor(string sourcePath, DatabaseCatalog catalog) : TSqlFragmentVisitor
    {
        public List<NotInNullableSubqueryFinding> Findings { get; } = [];

        public override void ExplicitVisit(QuerySpecification node)
        {
            InspectSearchCondition(node.WhereClause?.SearchCondition);
            base.ExplicitVisit(node);
        }

        public override void ExplicitVisit(UpdateStatement node)
        {
            InspectSearchCondition(node.UpdateSpecification.WhereClause?.SearchCondition);
            base.ExplicitVisit(node);
        }

        public override void ExplicitVisit(DeleteStatement node)
        {
            InspectSearchCondition(node.DeleteSpecification.WhereClause?.SearchCondition);
            base.ExplicitVisit(node);
        }

        private void InspectSearchCondition(BooleanExpression? searchCondition)
        {
            if (searchCondition is null)
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

            var (innerByAlias, innerOrdered) = FromScopeResolver.Resolve(subquerySpec.FromClause, catalog, EmptyResolvedViews, sourcePath, ledger: null, cteRelations: null, procScope: null);
            var innerProvenance = ScalarExpressionResolver.ResolveColumnReference(innerColumnRef, [(innerByAlias, innerOrdered)], sourcePath, ledger: null);
            if (innerProvenance is not ColumnProvenance.BaseColumn { Depth: 0 } innerColumn)
            {
                return;
            }

            var catalogColumn = catalog.Find(innerColumn.TableQualifiedName)?.FindColumn(innerColumn.ColumnName);
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

            var indexed = catalog.Find(innerColumn.TableQualifiedName)?.IsIndexedColumn(innerColumn.ColumnName) ?? false;

            Findings.Add(new NotInNullableSubqueryFinding(
                outerColumnName, innerColumn.TableQualifiedName, innerColumn.ColumnName, indexed,
                sourcePath, predicate.StartLine, predicate.StartColumn));
        }

        /// <summary>True iff the subquery's own WHERE unconditionally (via a top-level AND chain, never merely reachable through an OR branch) excludes NULLs from the exact same projected column - the single most common real-world fix for this bug, and firing on already-fixed code would be a visible false positive.</summary>
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

                var provenance = ScalarExpressionResolver.ResolveColumnReference(filterColumnRef, [(innerByAlias, innerOrdered)], sourcePath, ledger: null);
                if (provenance is ColumnProvenance.BaseColumn { Depth: 0 } filterColumn
                    && string.Equals(filterColumn.TableQualifiedName, tableQualifiedName, StringComparison.Ordinal)
                    && string.Equals(filterColumn.ColumnName, columnName, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }

            return false;
        }
    }
}
