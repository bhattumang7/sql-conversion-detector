using SilentScan.Core.Catalog;
using SilentScan.Core.TypeInference;

namespace SilentScan.Core.Predicates;

public static class ColumnstoreUnsupportedColumnTypeScanner
{
    public static IReadOnlyList<ColumnstoreUnsupportedColumnTypeFinding> Scan(DatabaseCatalog catalog)
    {
        var findings = new List<ColumnstoreUnsupportedColumnTypeFinding>();

        foreach (var table in catalog.Tables)
        {
            var sqlVariantColumns = table.Columns
                .Where(c => c.Type is { Category: SqlTypeCategory.SqlVariant })
                .ToList();
            if (sqlVariantColumns.Count == 0)
            {
                continue;
            }

            foreach (var index in table.Indexes)
            {
                if (!index.IsColumnstore)
                {
                    continue;
                }

                var hasExplicitColumnList = index.KeyColumns.Count > 0;
                var offendingColumns = hasExplicitColumnList
                    ? sqlVariantColumns.Where(c => index.KeyColumns.Contains(c.Name, catalog.IdentifierComparer))
                    : sqlVariantColumns;

                foreach (var column in offendingColumns)
                {
                    findings.Add(new ColumnstoreUnsupportedColumnTypeFinding(
                        table.QualifiedName, column.Name, column.Type!.ToString(), index.Name ?? "(unnamed)",
                        table.SourcePath, table.SourceLine));
                }
            }
        }

        return
        [
            .. findings
                .OrderBy(f => f.TableQualifiedName, StringComparer.Ordinal)
                .ThenBy(f => f.ColumnName, StringComparer.Ordinal)
                .ThenBy(f => f.IndexName, StringComparer.Ordinal),
        ];
    }
}
