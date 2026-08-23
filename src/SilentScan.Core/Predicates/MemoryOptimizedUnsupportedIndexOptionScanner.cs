using SilentScan.Core.Catalog;

namespace SilentScan.Core.Predicates;

public static class MemoryOptimizedUnsupportedIndexOptionScanner
{
    public static IReadOnlyList<MemoryOptimizedUnsupportedIndexOptionFinding> Scan(DatabaseCatalog catalog)
    {
        var findings = new List<MemoryOptimizedUnsupportedIndexOptionFinding>();

        foreach (var table in catalog.Tables)
        {
            if (!table.IsMemoryOptimized)
            {
                continue;
            }

            foreach (var index in table.Indexes)
            {
                findings.AddRange(ScanIndex(table, index));
            }
        }

        return
        [
            .. findings
                .OrderBy(f => f.TableQualifiedName, StringComparer.Ordinal)
                .ThenBy(f => f.IndexName, StringComparer.Ordinal)
                .ThenBy(f => f.Kind),
        ];
    }

    private static IEnumerable<MemoryOptimizedUnsupportedIndexOptionFinding> ScanIndex(CatalogTable table, CatalogIndex index)
    {
        if (index.IsColumnstore)
        {
            yield break;
        }

        var indexName = index.Name ?? "(unnamed)";

        if (index.IsClustered)
        {
            yield return new MemoryOptimizedUnsupportedIndexOptionFinding(
                table.QualifiedName, indexName, MemoryOptimizedUnsupportedIndexOptionKind.ClusteredIndex,
                table.SourcePath, table.SourceLine);
        }

        if (index.IncludedColumns.Count > 0)
        {
            yield return new MemoryOptimizedUnsupportedIndexOptionFinding(
                table.QualifiedName, indexName, MemoryOptimizedUnsupportedIndexOptionKind.IncludedColumns,
                table.SourcePath, table.SourceLine);
        }

        if (index.IsFiltered)
        {
            yield return new MemoryOptimizedUnsupportedIndexOptionFinding(
                table.QualifiedName, indexName, MemoryOptimizedUnsupportedIndexOptionKind.FilteredIndex,
                table.SourcePath, table.SourceLine);
        }
    }
}
