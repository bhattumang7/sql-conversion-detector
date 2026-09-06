using SilentScan.Core.Catalog;
using SilentScan.Core.TypeInference;

namespace SilentScan.Core.Predicates;

public static class MaxTypedColumnScanner
{
    public static IReadOnlyList<MaxTypedColumnFinding> Scan(DatabaseCatalog catalog)
    {
        var findings = new List<MaxTypedColumnFinding>();

        foreach (var table in catalog.Tables)
        {
            foreach (var column in table.Columns)
            {
                if (column.Type is not { } type)
                {
                    continue;
                }

                NonIndexableColumnFindingKind kind;

                if (type.IsMax || type.Category == SqlTypeCategory.Json)
                {
                    kind = NonIndexableColumnFindingKind.MaxLength;
                }
                else if (type.Category is SqlTypeCategory.Text or SqlTypeCategory.NText or SqlTypeCategory.Image)
                {
                    kind = NonIndexableColumnFindingKind.LegacyLargeObject;
                }
                else
                {
                    continue;
                }

                findings.Add(new MaxTypedColumnFinding(
                    table.QualifiedName, column.Name, type.ToString(), table.SourcePath, table.SourceLine, kind));
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
