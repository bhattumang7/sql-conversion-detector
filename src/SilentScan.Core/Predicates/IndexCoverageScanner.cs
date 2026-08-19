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

    private sealed class Visitor(string sourcePath, DatabaseCatalog catalog)
        : ConstrainedColumnStatementVisitor(sourcePath, catalog)
    {
        public List<IndexCoverageFinding> Findings { get; } = [];

        protected override void InspectStatement(ConstrainedStatement statement)
        {
            // Every base column of every table in scope referenced ANYWHERE in the whole
            // statement (SELECT list, WHERE, ORDER BY, GROUP BY, JOIN ON, HAVING) - the coverage
            // question needs this broader set, unlike CompositeIndexLeadingColumnScanner's own
            // narrower "referenced anywhere, used only to suppress" set.
            var allReferencedColumns = new HashSet<(string Table, string Column)>(TableColumnKeyComparer.Instance);
            var referenceVisitor = new BaseColumnResolver.ColumnReferenceCollector(SourcePath, statement.ScopeChain, allReferencedColumns);
            statement.Node.Accept(referenceVisitor);

            foreach (var table in statement.BaseTables)
            {
                InspectTable(table, statement.AndConstrainedColumns, allReferencedColumns, statement.Node);
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
                SourcePath, node.StartLine, node.StartColumn));
        }
    }
}
