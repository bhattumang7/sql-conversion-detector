using SilentScan.Core.Catalog;
using SilentScan.Core.TypeInference;

namespace SilentScan.Core.Predicates;

public static class MemoryOptimizedUnsupportedColumnTypeScanner
{
    private static readonly SqlTypeCategory[] UnsupportedCategories =
    [
        SqlTypeCategory.Xml,
        SqlTypeCategory.SqlVariant,
        SqlTypeCategory.Text,
        SqlTypeCategory.NText,
        SqlTypeCategory.Image,
        SqlTypeCategory.Timestamp,
    ];

    public static IReadOnlyList<MemoryOptimizedUnsupportedColumnTypeFinding> Scan(DatabaseCatalog catalog)
    {
        var findings = new List<MemoryOptimizedUnsupportedColumnTypeFinding>();

        foreach (var table in catalog.Tables)
        {
            if (!table.IsMemoryOptimized)
            {
                continue;
            }

            foreach (var column in table.Columns)
            {
                if (column.Type is not { } type || !UnsupportedCategories.Contains(type.Category))
                {
                    continue;
                }

                findings.Add(new MemoryOptimizedUnsupportedColumnTypeFinding(
                    table.QualifiedName, column.Name, type.ToString(), table.SourcePath, table.SourceLine));
            }
        }

        return
        [
            .. findings
                .OrderBy(f => f.TableQualifiedName, StringComparer.Ordinal)
                .ThenBy(f => f.ColumnName, StringComparer.Ordinal),
        ];
    }
}
