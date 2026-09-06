using SilentScan.Core.Catalog;
using SilentScan.Core.TypeInference;

namespace SilentScan.Core.Predicates;

public static class AlwaysEncryptedUnsupportedColumnScanner
{
    private static readonly HashSet<SqlTypeCategory> UnsupportedDataTypeCategories =
    [
        SqlTypeCategory.Xml,
        SqlTypeCategory.Json,
        SqlTypeCategory.Timestamp,
        SqlTypeCategory.Image,
        SqlTypeCategory.Text,
        SqlTypeCategory.NText,
        SqlTypeCategory.SqlVariant,
        SqlTypeCategory.HierarchyId,
        SqlTypeCategory.Geometry,
        SqlTypeCategory.Geography,
    ];

    public static IReadOnlyList<AlwaysEncryptedUnsupportedColumnFinding> Scan(DatabaseCatalog catalog)
    {
        var findings = new List<AlwaysEncryptedUnsupportedColumnFinding>();

        foreach (var table in catalog.Tables)
        {
            foreach (var column in table.Columns)
            {
                if (column.EncryptionType == ColumnEncryptionType.None)
                {
                    continue;
                }

                if (column.Type is { } type && UnsupportedDataTypeCategories.Contains(type.Category))
                {
                    findings.Add(new AlwaysEncryptedUnsupportedColumnFinding(
                        table.QualifiedName, column.Name, type.ToString(), AlwaysEncryptedUnsupportedColumnKind.UnsupportedDataType,
                        table.SourcePath, table.SourceLine));
                }

                if (column.IsIdentity)
                {
                    findings.Add(new AlwaysEncryptedUnsupportedColumnFinding(
                        table.QualifiedName, column.Name, column.Type?.ToString(), AlwaysEncryptedUnsupportedColumnKind.IdentityColumn,
                        table.SourcePath, table.SourceLine));
                }
            }
        }

        return
        [
            .. findings
                .OrderBy(f => f.TableQualifiedName, StringComparer.Ordinal)
                .ThenBy(f => f.ColumnName, StringComparer.Ordinal)
                .ThenBy(f => f.Kind),
        ];
    }
}
