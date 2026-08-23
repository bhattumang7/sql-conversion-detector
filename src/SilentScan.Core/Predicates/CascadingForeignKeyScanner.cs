using SilentScan.Core.Catalog;

namespace SilentScan.Core.Predicates;

public static class CascadingForeignKeyScanner
{
    public static IReadOnlyList<CascadingForeignKeyFinding> Scan(DatabaseCatalog catalog)
    {
        var findings = new List<CascadingForeignKeyFinding>();

        foreach (var fk in catalog.ForeignKeys
            .Where(fk => fk.DeleteAction != ReferentialAction.NoAction || fk.UpdateAction != ReferentialAction.NoAction)
            .DistinctBy(fk => fk.ConstraintName, StringComparer.OrdinalIgnoreCase))
        {
            var table = catalog.Find(fk.ReferencedTableQualifiedName);
            findings.Add(new CascadingForeignKeyFinding(
                fk.ConstraintName, fk.ParentTableQualifiedName, fk.ReferencedTableQualifiedName,
                fk.DeleteAction, fk.UpdateAction,
                table?.SourcePath ?? fk.ReferencedTableQualifiedName, table?.SourceLine ?? 0));
        }

        return
        [
            .. findings
                .OrderBy(f => f.ReferencedTableQualifiedName, StringComparer.Ordinal)
                .ThenBy(f => f.ConstraintName, StringComparer.Ordinal),
        ];
    }
}
