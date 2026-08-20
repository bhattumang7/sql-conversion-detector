using SilentScan.Core.Catalog;
using SilentScan.Core.TypeInference;

namespace SilentScan.Core.Predicates;

/// <summary>
/// Catalog-only pass - no AST walking. Two shapes, both oracle-confirmed
/// (see <see cref="ColumnstoreUnsupportedColumnTypeFinding"/>): a columnstore index with no
/// explicit column list (CREATE CLUSTERED COLUMNSTORE INDEX never takes one - it implicitly
/// covers every column) fails to deploy if the table has ANY SQL_VARIANT column, referenced or
/// not; a columnstore index WITH an explicit column list (CREATE NONCLUSTERED COLUMNSTORE INDEX
/// always takes one) fails to deploy only if a SQL_VARIANT column is actually named in that list
/// - a nonclustered columnstore index that simply omits the SQL_VARIANT column is a real, legal
/// shape and must not be flagged.
/// </summary>
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
                    ? sqlVariantColumns.Where(c => index.KeyColumns.Contains(c.Name, StringComparer.OrdinalIgnoreCase))
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
