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
                if (index.IsColumnstore)
                {
                    continue;
                }

                var indexName = index.Name ?? "(unnamed)";

                if (index.IsClustered)
                {
                    findings.Add(new MemoryOptimizedUnsupportedIndexOptionFinding(
                        table.QualifiedName, indexName, MemoryOptimizedUnsupportedIndexOptionKind.ClusteredIndex,
                        table.SourcePath, table.SourceLine));
                }

                if (index.IncludedColumns.Count > 0)
                {
                    findings.Add(new MemoryOptimizedUnsupportedIndexOptionFinding(
                        table.QualifiedName, indexName, MemoryOptimizedUnsupportedIndexOptionKind.IncludedColumns,
                        table.SourcePath, table.SourceLine));
                }

                if (index.IsFiltered)
                {
                    findings.Add(new MemoryOptimizedUnsupportedIndexOptionFinding(
                        table.QualifiedName, indexName, MemoryOptimizedUnsupportedIndexOptionKind.FilteredIndex,
                        table.SourcePath, table.SourceLine));
                }
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
}
