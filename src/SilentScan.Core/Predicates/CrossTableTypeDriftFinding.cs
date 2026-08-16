namespace SilentScan.Core.Predicates;

/// <summary>
/// A foreign-key column pair whose declared types and/or collations genuinely differ
/// (docs/detection-checklist.md Tier 1 "Join-key and cross-object type/collation mismatch":
/// cross-table same-name type drift, FK-linked half). A conversion SEED: every JOIN following
/// this FK relationship risks the same column-side conversion the shipped verdict stream already
/// classifies, but this finding fires from the catalog alone, independent of whether any scanned
/// query actually joins on it. Scoped to FK-linked pairs only - see
/// docs/detection-checklist.md's own note on why an unqualified same-name-column sweep across
/// every table pair (no FK, no observed join) was deliberately excluded as a precision risk.
/// </summary>
public sealed record CrossTableTypeDriftFinding(
    string ConstraintName,
    string ParentTableQualifiedName,
    string ParentColumnName,
    string ParentTypeDisplay,
    string ReferencedTableQualifiedName,
    string ReferencedColumnName,
    string ReferencedTypeDisplay,
    bool CollationDiffers,
    string SourcePath,
    int Line,
    FindingConfidence Confidence = FindingConfidence.High);
