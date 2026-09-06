using SilentScan.Core.Catalog;

namespace SilentScan.Core.Predicates;

public static class ComputedColumnIndexKeyScanner
{
    public static IReadOnlyList<ComputedColumnIndexKeyFinding> Scan(DatabaseCatalog catalog)
    {
        var findings = new List<ComputedColumnIndexKeyFinding>();

        foreach (var table in catalog.Tables)
        {
            foreach (var index in table.Indexes)
            {
                if (index.IsDisabled || index.IsHypothetical || index.IsColumnstore || index.IsXmlIndex || index.IsSpatialIndex || index.IsJsonIndex)
                {
                    continue;
                }

                foreach (var keyColumnName in index.KeyColumns)
                {
                    var column = table.FindColumn(keyColumnName, catalog.IdentifierComparer);
                    if (column is not { IsComputed: true, IsPersisted: false })
                    {
                        continue;
                    }

                    var kind = column.IsComputedNonDeterministic
                        ? ComputedColumnIndexKeyFindingKind.NonDeterministic
                        : column.IsComputedImprecise
                            ? ComputedColumnIndexKeyFindingKind.Imprecise
                            : (ComputedColumnIndexKeyFindingKind?)null;

                    if (kind is null)
                    {
                        continue;
                    }

                    findings.Add(new ComputedColumnIndexKeyFinding(
                        kind.Value, table.QualifiedName, column.Name, index.Name, table.SourcePath, table.SourceLine));
                }
            }
        }

        return
        [
            .. findings
                .OrderBy(f => f.SourcePath, StringComparer.Ordinal)
                .ThenBy(f => f.TableQualifiedName, StringComparer.Ordinal)
                .ThenBy(f => f.ColumnName, StringComparer.Ordinal)
                .ThenBy(f => f.IndexName, StringComparer.Ordinal),
        ];
    }
}
