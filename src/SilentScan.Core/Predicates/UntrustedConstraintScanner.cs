using SilentScan.Core.Catalog;

namespace SilentScan.Core.Predicates;

/// <summary>
/// Catalog-only pass (docs/detection-checklist.md Tier 2 "Lineage-metric findings": "Untrusted
/// (WITH NOCHECK) FK/CHECK constraints") - no AST walking, mirrors <see cref="MaxTypedColumnScanner"/>'s
/// own shape: every untrusted, non-disabled FK/CHECK constraint is flagged once, regardless of
/// whether any scanned query actually depends on the join elimination the optimizer forfeits.
/// A disabled constraint is not reported - it isn't silently weaker than it looks, it's openly off.
/// </summary>
public static class UntrustedConstraintScanner
{
    public static IReadOnlyList<UntrustedConstraintFinding> Scan(DatabaseCatalog catalog)
    {
        var findings = new List<UntrustedConstraintFinding>();

        foreach (var fk in catalog.ForeignKeys.Where(fk => fk.IsNotTrusted && !fk.IsDisabled)
            .DistinctBy(fk => fk.ConstraintName, StringComparer.OrdinalIgnoreCase))
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
