using Microsoft.SqlServer.TransactSql.ScriptDom;
using SilentScan.Core.Catalog;
using SilentScan.Core.Lineage;
using SilentScan.Core.Parsing;

namespace SilentScan.Core.Predicates;

public static class CompositeIndexLeadingColumnScanner
{
    private static readonly IReadOnlyDictionary<string, ResolvedRelation> EmptyResolvedViews = new Dictionary<string, ResolvedRelation>();

    public static IReadOnlyList<CompositeIndexLeadingColumnFinding> Scan(SqlParseResult parseResult, DatabaseCatalog catalog)
    {
        var visitor = new Visitor(parseResult.SourcePath, catalog);
        var walker = new ModuleWalker(parseResult.SourcePath, catalog, EmptyResolvedViews, ledger: null, currentProcScope: null, callerScopeByCalleeScope: null, rules: [visitor]);
        parseResult.Fragment.Accept(walker);
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
        public List<CompositeIndexLeadingColumnFinding> Findings { get; } = [];

        protected override void InspectStatement(ConstrainedStatement statement)
        {

            var anyReferencedColumns = new HashSet<(string Table, string Column)>(TableColumnKeyComparer.For(Catalog));
            var referenceVisitor = new BaseColumnResolver.ColumnReferenceCollector(SourcePath, statement.ScopeChain, anyReferencedColumns, Catalog);
            statement.WhereCondition?.Accept(referenceVisitor);
            foreach (var join in statement.JoinNodes)
            {
                join.SearchCondition.Accept(referenceVisitor);
            }

            foreach (var table in statement.BaseTables)
            {
                InspectTable(table, statement.AndConstrainedColumns, anyReferencedColumns, statement.Node);
            }
        }

        private void InspectTable(
            CatalogTable table,
            HashSet<ColumnProvenance.BaseColumn> andConstrainedColumns,
            HashSet<(string Table, string Column)> anyReferencedColumns,
            TSqlFragment node)
        {
            var usableIndexes = table.Indexes.Where(i => !i.IsFiltered && !i.IsColumnstore && !i.IsDisabled && i.KeyColumns.Count > 0).ToList();

            foreach (var index in usableIndexes.Where(i => i.KeyColumns.Count >= 2))
            {
                var leadingColumn = index.KeyColumns[0];
                if (anyReferencedColumns.Contains((table.QualifiedName, leadingColumn)))
                {

                    continue;
                }

                for (var position = 1; position < index.KeyColumns.Count; position++)
                {
                    var violatingColumn = index.KeyColumns[position];
                    if (!andConstrainedColumns.Contains(new ColumnProvenance.BaseColumn(table.QualifiedName, violatingColumn, Type: null)))
                    {
                        continue;
                    }

                    var hasAlternativeSeekPath = usableIndexes.Any(other =>
                        !ReferenceEquals(other, index)
                        && Catalog.IdentifierComparer.Equals(other.KeyColumns[0], violatingColumn));
                    if (hasAlternativeSeekPath)
                    {
                        continue;
                    }

                    Findings.Add(new CompositeIndexLeadingColumnFinding(
                        table.QualifiedName, index.Name, index.KeyColumns, violatingColumn, position,
                        SourcePath, node.StartLine, node.StartColumn));
                    break;
                }
            }
        }
    }
}
