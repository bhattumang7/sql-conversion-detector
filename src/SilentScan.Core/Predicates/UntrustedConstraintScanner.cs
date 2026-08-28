using SilentScan.Core.Catalog;

namespace SilentScan.Core.Predicates;

public static class UntrustedConstraintScanner
{
    public static IReadOnlyList<UntrustedConstraintFinding> Scan(DatabaseCatalog catalog)
    {
        var findings = new List<UntrustedConstraintFinding>();

        foreach (var fk in catalog.ForeignKeys.Where(fk => fk.IsNotTrusted && !fk.IsDisabled)
            .DistinctBy(fk => fk.ConstraintName, catalog.IdentifierComparer))
        {
            var table = catalog.Find(fk.ParentTableQualifiedName);
            findings.Add(new UntrustedConstraintFinding(
                UntrustedConstraintFindingKind.ForeignKey, fk.ConstraintName, fk.ParentTableQualifiedName,
                table?.SourcePath ?? fk.ParentTableQualifiedName, table?.SourceLine ?? 0));
        }

        foreach (var check in catalog.CheckConstraints.Where(c => c.IsNotTrusted && !c.IsDisabled))
        {
            var table = catalog.Find(check.TableQualifiedName);
            findings.Add(new UntrustedConstraintFinding(
                UntrustedConstraintFindingKind.CheckConstraint, check.ConstraintName, check.TableQualifiedName,
                table?.SourcePath ?? check.TableQualifiedName, table?.SourceLine ?? 0));
        }

        return
        [
            .. findings
                .OrderBy(f => f.Kind)
                .ThenBy(f => f.TableQualifiedName, StringComparer.Ordinal)
                .ThenBy(f => f.ConstraintName, StringComparer.Ordinal),
        ];
    }
}
