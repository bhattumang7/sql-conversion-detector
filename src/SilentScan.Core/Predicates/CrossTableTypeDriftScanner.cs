using SilentScan.Core.Catalog;
using SilentScan.Core.Rules;

namespace SilentScan.Core.Predicates;

/// <summary>
/// Catalog-only pass, FK-linked half of docs/detection-checklist.md Tier 1's cross-table
/// type-drift report - walks <see cref="DatabaseCatalog.ForeignKeys"/> (live-mode only; always
/// empty in file mode, see <see cref="DatabaseCatalog.AddForeignKey"/>) and flags a pair whose
/// resolved types genuinely differ (category, or - for the string family - collation, via
/// <see cref="VerdictClassifier.HasGenuineCollationMismatch"/>, the same check
/// <see cref="VerdictClassifier.ClassifyWithReason"/> itself uses). Length/precision-only
/// differences within the SAME category never fire: same-category length drift alone doesn't
/// defeat sargability (VerdictClassifier's own same-category rule), so flagging it here would
/// just be noise with no conversion-seed story behind it.
/// </summary>
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
