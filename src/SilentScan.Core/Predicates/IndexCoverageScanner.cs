using Microsoft.SqlServer.TransactSql.ScriptDom;
using SilentScan.Core.Catalog;
using SilentScan.Core.Lineage;
using SilentScan.Core.Parsing;

namespace SilentScan.Core.Predicates;

/// <summary>
/// docs/detection-checklist.md "DBA-script family sweep (2026-08-17)" §B "Index-coverage shapes" -
/// see <see cref="IndexCoverageFinding"/> for the full mechanism, oracle evidence, and the sibling
/// shape deliberately NOT shipped here. Shares <see cref="CompositeIndexLeadingColumnScanner"/>'s
/// own AND-flattened "which base columns does this statement genuinely constrain" walk almost
/// verbatim - a standalone scanner because this rule additionally needs every OTHER column of the
/// table referenced ANYWHERE (SELECT list, ORDER BY, GROUP BY included) to compute coverage, a
/// broader column-collection scope than that scanner's own "which columns are bound" question needs.
/// </summary>
public static class IndexCoverageScanner
{
    private static readonly IReadOnlyDictionary<string, ResolvedRelation> EmptyResolvedViews = new Dictionary<string, ResolvedRelation>();

    public static IReadOnlyList<IndexCoverageFinding> Scan(SqlParseResult parseResult, DatabaseCatalog catalog)
    {
        var visitor = new Visitor(parseResult.SourcePath, catalog);
        parseResult.Fragment.Accept(visitor);
        return
        [
            .. visitor.Findings
                .OrderBy(f => f.SourcePath, StringComparer.Ordinal)
                .ThenBy(f => f.Line)
                .ThenBy(f => f.Column)
                .ThenBy(f => f.IndexName, StringComparer.OrdinalIgnoreCase),
        ];
    }

    private sealed class Visitor(string sourcePath, DatabaseCatalog catalog) : TSqlFragmentVisitor
    {
        public List<IndexCoverageFinding> Findings { get; } = [];

        public override void ExplicitVisit(QuerySpecification node)
        {
            Inspect(node.FromClause, node.WhereClause?.SearchCondition, node);
            base.ExplicitVisit(node);
        }

        public override void ExplicitVisit(UpdateStatement node)
        {
            var spec = node.UpdateSpecification;
            var (byAlias, ordered) = FromScopeResolver.ResolveForDataModification(spec.Target, spec.FromClause, ResolutionContext());
            Inspect(byAlias, ordered, spec.FromClause, spec.WhereClause?.SearchCondition, node);
            base.ExplicitVisit(node);
        }

        public override void ExplicitVisit(DeleteStatement node)
        {
            var spec = node.DeleteSpecification;
            var (byAlias, ordered) = FromScopeResolver.ResolveForDataModification(spec.Target, spec.FromClause, ResolutionContext());
            Inspect(byAlias, ordered, spec.FromClause, spec.WhereClause?.SearchCondition, node);
            base.ExplicitVisit(node);
        }

        private FromScopeResolver.ResolutionContext ResolutionContext() =>
            new(catalog, EmptyResolvedViews, sourcePath, Ledger: null, CteRelations: null, ProcScope: null);

        private void Inspect(FromClause? fromClause, BooleanExpression? whereCondition, TSqlFragment node)
        {
            if (fromClause is null)
            {
                return;
            }

            var (byAlias, ordered) = FromScopeResolver.Resolve(fromClause, catalog, EmptyResolvedViews, sourcePath, ledger: null, cteRelations: null, procScope: null);
            Inspect(byAlias, ordered, fromClause, whereCondition, node);
        }

        private void Inspect(
            IReadOnlyDictionary<string, ScopeEntry> byAlias, IReadOnlyList<ScopeEntry> ordered,
            FromClause? fromClause, BooleanExpression? whereCondition, TSqlFragment node)
        {
            var baseTables = ordered
                .Where(e => !e.IsViewLayer && e.Relation.QualifiedName is not null)
                .Select(e => e.Relation.QualifiedName!)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Select(name => catalog.Find(name))
                .Where(t => t is not null && t.Kind == CatalogTableKind.Table)
                .Select(t => t!)
                .ToList();

            if (baseTables.Count == 0)
            {
                return;
            }

            var scopeChain = new List<(IReadOnlyDictionary<string, ScopeEntry> ByAlias, IReadOnlyList<ScopeEntry> Ordered)> { (byAlias, ordered) };
            var joinNodes = fromClause is null ? [] : fromClause.TableReferences.SelectMany(PredicateTreeWalker.FlattenJoinNodes).ToList();

            // AND-constrained columns (a real seek-enabling comparison) - identical discipline to
            // CompositeIndexLeadingColumnScanner.
            var andConstrainedColumns = joinNodes
                .SelectMany(j => PredicateTreeWalker.FlattenAnd(j.SearchCondition))
                .Concat(PredicateTreeWalker.FlattenAnd(whereCondition))
                .OfType<BooleanComparisonExpression>()
                .SelectMany(c => ResolveBothSides(c, scopeChain))
                .ToHashSet();

            // Every base column of every table in scope referenced ANYWHERE in the whole
            // statement (SELECT list, WHERE, ORDER BY, GROUP BY, JOIN ON, HAVING) - the coverage
            // question needs this broader set, unlike CompositeIndexLeadingColumnScanner's own
            // narrower "referenced anywhere, used only to suppress" set.
            var allReferencedColumns = new HashSet<(string Table, string Column)>();
            var referenceVisitor = new ColumnReferenceCollector(sourcePath, scopeChain, allReferencedColumns);
            node.Accept(referenceVisitor);

            foreach (var table in baseTables)
            {
                InspectTable(table, andConstrainedColumns, allReferencedColumns, node);
            }
        }

        private void InspectTable(
            CatalogTable table,
            HashSet<(string Table, string Column)> andConstrainedColumns,
            HashSet<(string Table, string Column)> allReferencedColumns,
            TSqlFragment node)
        {
            var constrainedColumnsOnTable = andConstrainedColumns
                .Where(c => string.Equals(c.Table, table.QualifiedName, StringComparison.OrdinalIgnoreCase))
                .Select(c => c.Column)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            if (constrainedColumnsOnTable.Count == 0)
            {
                return;
            }

            var usableNonclusteredIndexes = table.Indexes
                .Where(i => !i.IsFiltered && !i.IsColumnstore && !i.IsDisabled && !i.IsClustered && i.KeyColumns.Count > 0)
                .ToList();

            var candidateIndexes = usableNonclusteredIndexes
                .Where(i => constrainedColumnsOnTable.Contains(i.KeyColumns[0]))
                .ToList();

            // The hard precision guard: exactly one real candidate seek path, never more (a real
            // alternative access path the optimizer could pick instead) and never zero (nothing to
            // report a lookup against).
            if (candidateIndexes.Count != 1)
            {
                return;
            }

            var index = candidateIndexes[0];

            // A nonclustered index's own leaf row always carries the table's clustering key as
            // its row locator (that IS the bookmark a lookup follows) - the engine gives every
            // nonclustered index this column set for free, regardless of its own KeyColumns/
            // IncludedColumns. IsClustered is live-mode only (see CatalogIndex's own doc comment),
            // so file mode falls back to the PRIMARY KEY index's own key columns - SQL Server's
            // real default is CLUSTERED unless a script says otherwise, and getting this wrong in
            // file mode only means under-reporting (a real lookup missed), never a false claim,
            // which is the safe direction CLAUDE.md's precision-first rule asks for.
            var clusteringKeyColumns =
                table.Indexes.FirstOrDefault(i => i.IsClustered && !i.IsColumnstore)?.KeyColumns
                ?? table.Indexes.FirstOrDefault(i => i.Kind == CatalogIndexKind.PrimaryKey)?.KeyColumns
                ?? [];

            var indexColumns = index.KeyColumns
                .Concat(index.IncludedColumns)
                .Concat(clusteringKeyColumns)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            var uncoveredColumns = allReferencedColumns
                .Where(c => string.Equals(c.Table, table.QualifiedName, StringComparison.OrdinalIgnoreCase))
                .Select(c => c.Column)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Where(c => !indexColumns.Contains(c))
                .OrderBy(c => c, StringComparer.OrdinalIgnoreCase)
                .ToList();

            if (uncoveredColumns.Count == 0)
            {
                return;
            }

            Findings.Add(new IndexCoverageFinding(
                IndexCoverageFindingKind.KeyLookupProneIndex,
                table.QualifiedName, index.Name, index.KeyColumns, index.IncludedColumns, uncoveredColumns,
                sourcePath, node.StartLine, node.StartColumn));
        }

        private IEnumerable<(string Table, string Column)> ResolveBothSides(
            BooleanComparisonExpression predicate,
            IReadOnlyList<(IReadOnlyDictionary<string, ScopeEntry> ByAlias, IReadOnlyList<ScopeEntry> Ordered)> scopeChain)
        {
            foreach (var side in new[] { predicate.FirstExpression, predicate.SecondExpression })
            {
                if (ResolveBaseColumn(side, scopeChain) is { } resolved)
                {
                    yield return resolved;
                }
            }
        }

        private (string Table, string Column)? ResolveBaseColumn(
            ScalarExpression expression,
            IReadOnlyList<(IReadOnlyDictionary<string, ScopeEntry> ByAlias, IReadOnlyList<ScopeEntry> Ordered)> scopeChain)
        {
            if (expression is not ColumnReferenceExpression columnRef)
            {
                return null;
            }

            var provenance = ScalarExpressionResolver.ResolveColumnReference(columnRef, scopeChain, sourcePath, ledger: null);
            return provenance is ColumnProvenance.BaseColumn { Depth: 0 } baseColumn
                ? (baseColumn.TableQualifiedName, baseColumn.ColumnName)
                : null;
        }

        /// <summary>Collects every base-column reference reachable anywhere under the whole
        /// statement fragment - deliberately broad (SELECT list, WHERE, ORDER BY, GROUP BY, JOIN
        /// ON, HAVING all count), since the coverage question this rule asks needs every column the
        /// statement touches on the table, not just the ones that constrain a predicate.</summary>
        private sealed class ColumnReferenceCollector(
            string sourcePath,
            IReadOnlyList<(IReadOnlyDictionary<string, ScopeEntry> ByAlias, IReadOnlyList<ScopeEntry> Ordered)> scopeChain,
            HashSet<(string Table, string Column)> sink) : TSqlFragmentVisitor
        {
            public override void ExplicitVisit(ColumnReferenceExpression node)
            {
                if (node.ColumnType != ColumnType.Wildcard)
                {
                    var provenance = ScalarExpressionResolver.ResolveColumnReference(node, scopeChain, sourcePath, ledger: null);
                    if (provenance is ColumnProvenance.BaseColumn { Depth: 0 } baseColumn)
                    {
                        sink.Add((baseColumn.TableQualifiedName, baseColumn.ColumnName));
                    }
                }

                base.ExplicitVisit(node);
            }
        }
    }
}
