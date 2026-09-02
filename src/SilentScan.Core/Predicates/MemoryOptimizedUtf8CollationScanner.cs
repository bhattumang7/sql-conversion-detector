using SilentScan.Core.Catalog;
using SilentScan.Core.TypeInference;

namespace SilentScan.Core.Predicates;

public static class MemoryOptimizedUtf8CollationScanner
{
    private static readonly HashSet<SqlTypeCategory> NonUnicodeCharacterCategories =
    [
        SqlTypeCategory.Char,
        SqlTypeCategory.VarChar,
    ];

    public static IReadOnlyList<MemoryOptimizedUtf8CollationFinding> Scan(DatabaseCatalog catalog)
    {
        var findings = new List<MemoryOptimizedUtf8CollationFinding>();

        foreach (var table in catalog.Tables)
        {
            if (!table.IsMemoryOptimized)
            {
                continue;
            }

            foreach (var column in table.Columns)
            {
                if (column.Type is not { } type
                    || !NonUnicodeCharacterCategories.Contains(type.Category)
                    || type.Collation is not { } collation
                    || !collation.IsUtf8)
                {
                    continue;
                }

                findings.Add(new MemoryOptimizedUtf8CollationFinding(
                    table.QualifiedName, column.Name, type.ToString(), collation.Name, table.SourcePath, table.SourceLine));
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
