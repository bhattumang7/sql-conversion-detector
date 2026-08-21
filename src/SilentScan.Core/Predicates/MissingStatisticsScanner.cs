using SilentScan.Core.Catalog;
using SilentScan.Core.Parsing;

namespace SilentScan.Core.Predicates;

public static class MissingStatisticsScanner
{
    public static IReadOnlyList<MissingStatisticsFinding> Scan(SqlParseResult parseResult, DatabaseCatalog catalog)
    {
        if (catalog.IsAutoCreateStatsOn != false)
        {
            return [];
        }

        var visitor = new Visitor(parseResult.SourcePath, catalog);
        parseResult.Fragment.Accept(visitor);
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
                    .Where(c => string.Equals(c.TableQualifiedName, table.QualifiedName, StringComparison.OrdinalIgnoreCase))
                    .Select(c => c.ColumnName)
                    .Distinct(StringComparer.OrdinalIgnoreCase);

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

        private static bool HasApplicableStatistic(CatalogTable table, string columnName) =>
            table.EffectiveStatistics.Any(s =>
                s.KeyColumns.Count > 0 && string.Equals(s.KeyColumns[0], columnName, StringComparison.OrdinalIgnoreCase));
    }
}
