using SilentScan.Core.Catalog;

namespace SilentScan.Core.Predicates;

public static class MemoryOptimizedForeignKeyScanner
{
    public static IReadOnlyList<MemoryOptimizedForeignKeyFinding> Scan(DatabaseCatalog catalog)
    {
        var findings = new List<MemoryOptimizedForeignKeyFinding>();

        foreach (var fk in catalog.ForeignKeys.DistinctBy(fk => fk.ConstraintName, catalog.IdentifierComparer))
        {
            var parentTable = catalog.Find(fk.ParentTableQualifiedName);
            var referencedTable = catalog.Find(fk.ReferencedTableQualifiedName);
            if (parentTable is null || referencedTable is null)
            {
                continue;
            }

            MemoryOptimizedForeignKeyFindingKind? kind = null;

            if (parentTable.IsMemoryOptimized != referencedTable.IsMemoryOptimized)
            {
                kind = MemoryOptimizedForeignKeyFindingKind.CrossStorageForeignKey;
            }
            else if (parentTable.IsMemoryOptimized && referencedTable.IsMemoryOptimized
                && (fk.DeleteAction != ReferentialAction.NoAction || fk.UpdateAction != ReferentialAction.NoAction))
            {
                kind = MemoryOptimizedForeignKeyFindingKind.ReferentialAction;
            }

            if (kind is null)
            {
                continue;
            }

            findings.Add(new MemoryOptimizedForeignKeyFinding(
                fk.ConstraintName, fk.ParentTableQualifiedName, fk.ReferencedTableQualifiedName, kind.Value,
                parentTable.SourcePath, parentTable.SourceLine));
        }

        return
        [
            .. findings
                .OrderBy(f => f.ParentTableQualifiedName, StringComparer.Ordinal)
                .ThenBy(f => f.ConstraintName, StringComparer.Ordinal),
        ];
    }
}
