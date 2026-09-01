using SilentScan.Core.Catalog;
using SilentScan.Core.TypeInference;

namespace SilentScan.Core.Predicates;

public static class SparseColumnDisallowedTypeScanner
{
    private static readonly HashSet<SqlTypeCategory> DisallowedCategories =
    [
        SqlTypeCategory.Text,
        SqlTypeCategory.NText,
        SqlTypeCategory.Image,
        SqlTypeCategory.Geometry,
        SqlTypeCategory.Geography,
        SqlTypeCategory.Timestamp,
    ];

    public static IReadOnlyList<SparseColumnDisallowedTypeFinding> Scan(DatabaseCatalog catalog)
    {
        var findings = new List<SparseColumnDisallowedTypeFinding>();

        foreach (var table in catalog.Tables)
        {
            foreach (var column in table.Columns)
            {
                if (!column.IsSparse || column.Type is not { } type || !DisallowedCategories.Contains(type.Category))
                {
                    continue;
                }

                findings.Add(new SparseColumnDisallowedTypeFinding(
                    table.QualifiedName, column.Name, type.ToString(), table.SourcePath, table.SourceLine, Column: 1));
            }
        }

        return
        [
            .. findings
                .OrderBy(f => f.SourcePath, StringComparer.Ordinal)
                .ThenBy(f => f.Line)
                .ThenBy(f => f.ColumnName, StringComparer.Ordinal),
        ];
    }
}
