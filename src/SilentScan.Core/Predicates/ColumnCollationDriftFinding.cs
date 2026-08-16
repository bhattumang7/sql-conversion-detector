namespace SilentScan.Core.Predicates;

/// <summary>
/// A string-family column whose resolved collation differs from the database's own default
/// collation (docs/detection-checklist.md Tier 1 "Join-key and cross-object type/collation
/// mismatch": "column collation != database collation"). Catalog-only - a schema-side conversion
/// SEED, not a predicate finding: this column has not yet been compared to anything, but any
/// future comparison against a column/literal carrying the database's own default collation risks
/// the same <see cref="CollationConflictFinding"/> (a genuine mismatch, compile error) or
/// <see cref="Rules.Verdict.ScanForced"/> (a literal/parameter, which always takes the column's
/// collation and forces CONVERT_IMPLICIT) outcome those two finding kinds already report once a
/// query actually reaches this column. <see cref="IsTempObject"/> distinguishes a temp
/// table/table variable column (whose relevant baseline is tempdb's own collation, not the user
/// database's - the classic cause of collation-conflict errors joining a temp object to a user
/// table) from an ordinary base table column.
/// </summary>
public sealed record ColumnCollationDriftFinding(
    string TableQualifiedName,
    string ColumnName,
    string ColumnCollationName,
    string BaselineCollationName,
    bool IsTempObject,
    string SourcePath,
    int Line,
    FindingConfidence Confidence = FindingConfidence.High);
