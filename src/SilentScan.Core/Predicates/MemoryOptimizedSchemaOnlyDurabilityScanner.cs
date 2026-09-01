using SilentScan.Core.Catalog;

namespace SilentScan.Core.Predicates;

public static class MemoryOptimizedSchemaOnlyDurabilityScanner
{
    public static IReadOnlyList<MemoryOptimizedSchemaOnlyDurabilityFinding> Scan(DatabaseCatalog catalog)
    {
        var findings = new List<MemoryOptimizedSchemaOnlyDurabilityFinding>();

        foreach (var table in catalog.Tables)
        {
            if (!table.IsMemoryOptimized || !table.IsSchemaOnlyDurability)
            {
                continue;
            }

            findings.Add(new MemoryOptimizedSchemaOnlyDurabilityFinding(table.QualifiedName, table.SourcePath, table.SourceLine));
        }

        return
        [
            .. findings.OrderBy(f => f.TableQualifiedName, StringComparer.Ordinal),
        ];
    }
}
