using SilentScan.Core.Catalog;
using SilentScan.Core.TypeInference;

namespace SilentScan.Core.Predicates;

public static class AnsiPaddingOffColumnScanner
{
    public static IReadOnlyList<AnsiPaddingOffColumnFinding> Scan(DatabaseCatalog catalog)
    {
        var findings = new List<AnsiPaddingOffColumnFinding>();

        foreach (var table in catalog.Tables)
        {
            foreach (var column in table.Columns)
            {
                if (column.IsAnsiPadded)
                {
                    continue;
                }

                if (column.Type is not { Category: SqlTypeCategory.VarChar or SqlTypeCategory.NVarChar or SqlTypeCategory.VarBinary })
                {
                    continue;
                }

                findings.Add(new AnsiPaddingOffColumnFinding(
                    table.QualifiedName, column.Name, table.SourcePath, table.SourceLine));
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
