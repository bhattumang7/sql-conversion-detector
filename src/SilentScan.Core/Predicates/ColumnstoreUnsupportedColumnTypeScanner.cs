using SilentScan.Core.Catalog;
using SilentScan.Core.TypeInference;

namespace SilentScan.Core.Predicates;

public static class ColumnstoreUnsupportedColumnTypeScanner
{
    private static readonly HashSet<SqlTypeCategory> AlwaysUnsupportedCategories =
    [
        SqlTypeCategory.SqlVariant,
        SqlTypeCategory.Xml,
        SqlTypeCategory.HierarchyId,
        SqlTypeCategory.Geometry,
        SqlTypeCategory.Geography,
        SqlTypeCategory.NText,
        SqlTypeCategory.Text,
        SqlTypeCategory.Image,
        SqlTypeCategory.Timestamp,
    ];

    private static readonly HashSet<SqlTypeCategory> MaxLengthNonclusteredOnlyCategories =
    [
        SqlTypeCategory.VarChar,
        SqlTypeCategory.NVarChar,
        SqlTypeCategory.VarBinary,
    ];

    public static IReadOnlyList<ColumnstoreUnsupportedColumnTypeFinding> Scan(DatabaseCatalog catalog)
    {
        var findings = new List<ColumnstoreUnsupportedColumnTypeFinding>();

        foreach (var table in catalog.Tables)
        {
            foreach (var index in table.Indexes.Where(i => i.IsColumnstore))
            {
                findings.AddRange(ScanIndex(catalog, table, index));
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

    private static IEnumerable<ColumnstoreUnsupportedColumnTypeFinding> ScanIndex(
        DatabaseCatalog catalog, CatalogTable table, CatalogIndex index)
    {
        var hasExplicitColumnList = index.KeyColumns.Count > 0;
        var candidateColumns = hasExplicitColumnList
            ? table.Columns.Where(c => index.KeyColumns.Contains(c.Name, catalog.IdentifierComparer))
            : table.Columns;

        foreach (var column in candidateColumns)
        {
            if (column.Type is not { } type || !IsUnsupported(type, index.IsClustered))
            {
                continue;
            }

            yield return new ColumnstoreUnsupportedColumnTypeFinding(
                table.QualifiedName, column.Name, type.ToString(), index.Name ?? "(unnamed)",
                table.SourcePath, table.SourceLine);
        }
    }

    private static bool IsUnsupported(SqlType type, bool isClustered) =>
        AlwaysUnsupportedCategories.Contains(type.Category)
        || (!isClustered && type.IsMax && MaxLengthNonclusteredOnlyCategories.Contains(type.Category));
}
