using SilentScan.Core.Catalog;
using SilentScan.Core.TypeInference;

namespace SilentScan.Core.Predicates;

public static class ColumnCollationDriftScanner
{
    public static IReadOnlyList<ColumnCollationDriftFinding> Scan(DatabaseCatalog catalog)
    {
        var findings = new List<ColumnCollationDriftFinding>();

        foreach (var table in catalog.Tables)
        {
            var isTempObject = table.Kind is CatalogTableKind.TemporaryTable or CatalogTableKind.TableVariable;
            var baseline = isTempObject ? catalog.EffectiveTempdbCollation : catalog.DefaultCollation;
            if (baseline is null)
            {
                continue;
            }

            foreach (var column in table.Columns)
            {
                if (column.Type is not { IsStringFamily: true, Collation: { } columnCollation })
                {
                    continue;
                }

                if (!string.Equals(columnCollation.Name, baseline.Name, StringComparison.OrdinalIgnoreCase))
                {
                    findings.Add(new ColumnCollationDriftFinding(
                        table.QualifiedName, column.Name, columnCollation.Name, baseline.Name,
                        isTempObject, table.SourcePath, table.SourceLine));
                }
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
