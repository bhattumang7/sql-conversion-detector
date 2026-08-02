namespace SilentScan.Core.Predicates;

/// <summary>
/// Two real, indexed-or-not base/declared columns compared directly against each other, both
/// string-family, both with genuinely different resolved collations, and neither carrying an
/// explicit COLLATE clause of its own (a self-differing explicit COLLATE is reported instead as
/// an <see cref="ExpressionDerivedFinding"/> - see <see cref="Lineage.ScalarExpressionResolver"/>'s
/// ApplyExplicitCollate). Oracle-verified directly (Docker SQL Server): this does not compile -
/// SQL Server raises Msg 468 ("Cannot resolve the collation conflict") - so it is reported as
/// its own finding kind rather than a <see cref="Rules.Verdict"/>: there is no seek/scan
/// question to answer for a predicate that never executes. Distinct from a column-vs-literal
/// collation mismatch (a literal is always "coercible default" and never conflicts - that case
/// compiles fine and is a real ScanForced <see cref="TypedPredicateFinding"/> instead).
/// </summary>
public sealed record CollationConflictFinding(
    string FirstTableQualifiedName,
    string FirstColumnName,
    string FirstCollationName,
    string SecondTableQualifiedName,
    string SecondColumnName,
    string SecondCollationName,
    string Operator,
    string SourcePath,
    int Line,
    int ColumnPosition,
    SourceSpan? DynamicSqlCallSite = null);
