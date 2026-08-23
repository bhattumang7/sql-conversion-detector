using Microsoft.SqlServer.TransactSql.ScriptDom;
using SilentScan.Core.Catalog;
using SilentScan.Core.Lineage;
using SilentScan.Core.Parsing;

namespace SilentScan.Core.Predicates;

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
            HashSet<ColumnProvenance.BaseColumn> andConstrainedColumns,
            HashSet<(string Table, string Column)> allReferencedColumns,
            TSqlFragment node)
        {
            var constrainedColumnsOnTable = andConstrainedColumns
                .Where(c => string.Equals(c.TableQualifiedName, table.QualifiedName, StringComparison.OrdinalIgnoreCase))
                .Select(c => c.ColumnName)
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

            if (candidateIndexes.Count != 1)
            {
                return;
            }

            var index = candidateIndexes[0];

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
