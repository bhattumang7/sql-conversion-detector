using SilentScan.Core.Catalog;
using SilentScan.Core.Lineage;
using SilentScan.Core.Parsing;

namespace SilentScan.Core.Predicates;

public static class MissingStatisticsScanner
{
    private static readonly IReadOnlyDictionary<string, ResolvedRelation> EmptyResolvedViews = new Dictionary<string, ResolvedRelation>();

    public static IReadOnlyList<MissingStatisticsFinding> Scan(SqlParseResult parseResult, DatabaseCatalog catalog)
    {
        if (catalog.IsAutoCreateStatsOn != false)
        {
            return [];
        }

        var visitor = new Visitor(parseResult.SourcePath, catalog);
        var walker = new ModuleWalker(parseResult.SourcePath, catalog, EmptyResolvedViews, ledger: null, currentProcScope: null, callerScopeByCalleeScope: null, rules: [visitor]);
        parseResult.Fragment.Accept(walker);
        return
        [
            .. visitor.Findings
                .OrderBy(f => f.SourcePath, StringComparer.Ordinal)
                .ThenBy(f => f.Line)
                .ThenBy(f => f.Column)
                .ThenBy(f => f.TableQualifiedName, StringComparer.OrdinalIgnoreCase)
                .ThenBy(f => f.ColumnName, StringComparer.OrdinalIgnoreCase),
        ];
    }

    private sealed class Visitor(string sourcePath, DatabaseCatalog catalog)
        : ConstrainedColumnStatementVisitor(sourcePath, catalog)
    {
        public List<MissingStatisticsFinding> Findings { get; } = [];

        protected override void InspectStatement(ConstrainedStatement statement)
        {
            foreach (var table in statement.BaseTables)
            {
                var constrainedColumns = statement.AndConstrainedColumns
                    .Where(c => Catalog.IdentifierComparer.Equals(c.TableQualifiedName, table.QualifiedName))
                    .Select(c => c.ColumnName)
                    .Distinct(Catalog.IdentifierComparer);

                foreach (var columnName in constrainedColumns)
                {
                    if (HasApplicableStatistic(table, columnName))
                    {
                        continue;
                    }

                    Findings.Add(new MissingStatisticsFinding(
                        table.QualifiedName, columnName, SourcePath, statement.Node.StartLine, statement.Node.StartColumn));
                }
            }
        }

        private bool HasApplicableStatistic(CatalogTable table, string columnName) =>
            table.EffectiveStatistics.Any(s =>
                s.KeyColumns.Count > 0 && Catalog.IdentifierComparer.Equals(s.KeyColumns[0], columnName));
    }
}
