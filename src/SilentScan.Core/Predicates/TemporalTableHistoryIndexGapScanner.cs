using SilentScan.Core.Catalog;

namespace SilentScan.Core.Predicates;

public static class TemporalTableHistoryIndexGapScanner
{
    public static IReadOnlyList<TemporalTableHistoryIndexGapFinding> Scan(DatabaseCatalog catalog)
    {
        var findings = new List<TemporalTableHistoryIndexGapFinding>();

        foreach (var pair in catalog.TemporalTablePairs)
        {
            var current = catalog.Find(pair.CurrentTableQualifiedName);
            var history = catalog.Find(pair.HistoryTableQualifiedName);
            if (current is null || history is null)
            {

                continue;
            }

            foreach (var index in current.Indexes.Where(IsComparableIndex))
            {
                var hasMatch = history.Indexes.Any(h => IsComparableIndex(h) && SameKeyColumns(index, h, catalog.IdentifierComparer));
                if (hasMatch)
                {
                    continue;
                }

                findings.Add(new TemporalTableHistoryIndexGapFinding(
                    current.QualifiedName, history.QualifiedName, index.Name, index.KeyColumns,
                    current.SourcePath, current.SourceLine));
            }
        }

        return
        [
            .. findings
                .OrderBy(f => f.CurrentTableQualifiedName, StringComparer.Ordinal)
                .ThenBy(f => f.CurrentIndexName, StringComparer.Ordinal),
        ];
    }

    private static bool IsComparableIndex(CatalogIndex index) =>
        index.Kind == CatalogIndexKind.Index && !index.IsFiltered && !index.IsColumnstore && !index.IsDisabled
        && index.KeyColumns.Count > 0;

    private static bool SameKeyColumns(CatalogIndex current, CatalogIndex candidate, StringComparer identifierComparer) =>
        current.KeyColumns.SequenceEqual(candidate.KeyColumns, identifierComparer);
}
