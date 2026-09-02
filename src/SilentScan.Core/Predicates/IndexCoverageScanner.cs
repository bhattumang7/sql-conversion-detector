using Microsoft.SqlServer.TransactSql.ScriptDom;
using SilentScan.Core.Catalog;
using SilentScan.Core.Lineage;
using SilentScan.Core.Parsing;

namespace SilentScan.Core.Predicates;

public static class IndexCoverageScanner
{
    private static readonly IReadOnlyDictionary<string, ResolvedRelation> EmptyResolvedViews = new Dictionary<string, ResolvedRelation>();

    public static IReadOnlyList<IndexCoverageFinding> Scan(SqlParseResult parseResult, DatabaseCatalog catalog)
    {
        var visitor = CreateRule(parseResult.SourcePath, catalog);
        var walker = new ModuleWalker(parseResult.SourcePath, catalog, EmptyResolvedViews, rules: [visitor]);
        parseResult.Fragment.Accept(walker);
    return Harvest(visitor);
    }
    internal static Visitor CreateRule(string sourcePath, DatabaseCatalog catalog) => new(sourcePath, catalog);

    internal static IReadOnlyList<IndexCoverageFinding> Harvest(Visitor visitor) =>
            [
            .. visitor.Findings
                .OrderBy(f => f.SourcePath, StringComparer.Ordinal)
                .ThenBy(f => f.Line)
                .ThenBy(f => f.Column)
                .ThenBy(f => f.IndexName, StringComparer.OrdinalIgnoreCase),
        ];


    internal sealed class Visitor(string sourcePath, DatabaseCatalog catalog)
        : ConstrainedColumnStatementVisitor(sourcePath, catalog)
    {
        public List<IndexCoverageFinding> Findings { get; } = [];

        protected override void InspectStatement(ConstrainedStatement statement)
        {

            var allReferencedColumns = new HashSet<(string Table, string Column)>(TableColumnKeyComparer.For(Catalog));
            var referenceVisitor = new BaseColumnResolver.ColumnReferenceCollector(SourcePath, statement.ScopeChain, allReferencedColumns, Catalog);
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
                .Where(c => Catalog.IdentifierComparer.Equals(c.TableQualifiedName, table.QualifiedName))
                .Select(c => c.ColumnName)
                .ToHashSet(Catalog.IdentifierComparer);

            if (constrainedColumnsOnTable.Count == 0)
            {
                return;
            }

            var usableNonclusteredIndexes = table.Indexes
                .Where(i => !i.IsFiltered && !i.IsColumnstore && !i.IsDisabled && !i.IsClustered && !i.IsJsonIndex && i.KeyColumns.Count > 0)
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
                ?? [];

            var indexColumns = index.KeyColumns
                .Concat(index.IncludedColumns)
                .Concat(clusteringKeyColumns)
                .ToHashSet(Catalog.IdentifierComparer);

            var uncoveredColumns = allReferencedColumns
                .Where(c => Catalog.IdentifierComparer.Equals(c.Table, table.QualifiedName))
                .Select(c => c.Column)
                .Distinct(Catalog.IdentifierComparer)
                .Where(c => !indexColumns.Contains(c))
                .OrderBy(c => c, Catalog.IdentifierComparer)
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
