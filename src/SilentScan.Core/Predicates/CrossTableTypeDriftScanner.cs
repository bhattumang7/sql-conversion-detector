using SilentScan.Core.Catalog;
using SilentScan.Core.Rules;

namespace SilentScan.Core.Predicates;

public static class CrossTableTypeDriftScanner
{
    public static IReadOnlyList<CrossTableTypeDriftFinding> Scan(DatabaseCatalog catalog)
    {
        var findings = new List<CrossTableTypeDriftFinding>();

        foreach (var fk in catalog.ForeignKeys)
        {
            var parentTable = catalog.Find(fk.ParentTableQualifiedName);
            var parentType = parentTable?.FindColumn(fk.ParentColumnName)?.Type;
            var referencedType = catalog.Find(fk.ReferencedTableQualifiedName)?.FindColumn(fk.ReferencedColumnName)?.Type;
            if (parentTable is null || parentType is null || referencedType is null)
            {
                continue;
            }

            var collationDiffers = VerdictClassifier.HasGenuineCollationMismatch(parentType, referencedType);

            if (parentType.Category != referencedType.Category || collationDiffers)
            {
                findings.Add(new CrossTableTypeDriftFinding(
                    fk.ConstraintName,
                    fk.ParentTableQualifiedName, fk.ParentColumnName, parentType.ToString(),
                    fk.ReferencedTableQualifiedName, fk.ReferencedColumnName, referencedType.ToString(),
                    collationDiffers, parentTable.SourcePath, parentTable.SourceLine));
            }
        }

        return
        [
            .. findings
                .OrderBy(f => f.ParentTableQualifiedName, StringComparer.Ordinal)
                .ThenBy(f => f.ParentColumnName, StringComparer.Ordinal),
        ];
    }
}
