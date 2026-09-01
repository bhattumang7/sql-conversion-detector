using SilentScan.Core.Catalog;
using SilentScan.Core.TypeInference;

namespace SilentScan.Core.Predicates;

public static class LegacyLobUtf8CollationScanner
{
    private static readonly HashSet<SqlTypeCategory> LegacyLobCategories =
    [
        SqlTypeCategory.NText,
        SqlTypeCategory.Text,
    ];

    public static IReadOnlyList<LegacyLobUtf8CollationFinding> Scan(DatabaseCatalog catalog)
    {
        var findings = new List<LegacyLobUtf8CollationFinding>();

        foreach (var table in catalog.Tables)
        {
            foreach (var column in table.Columns)
            {
                if (column.Type is not { } type
                    || !LegacyLobCategories.Contains(type.Category)
                    || type.Collation is not { } collation
                    || !(collation.IsUtf8 || collation.IsSupplementaryCharacterAware))
                {
                    continue;
                }

                findings.Add(new LegacyLobUtf8CollationFinding(
                    table.QualifiedName, column.Name, type.ToString(), collation.Name, table.SourcePath, table.SourceLine, Column: 1));
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
