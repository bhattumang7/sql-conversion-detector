using SilentScan.Core.Catalog;

namespace SilentScan.Core.Rules;

/// <summary>
/// Pure per-rule decisions for Tier-1 syntactic sargability findings, extracted out of
/// <c>NonSargablePredicateScanner</c>'s visitor (docs/detection-checklist.md "Engineering debt" -
/// separating rule decisions from ScriptDom traversal mechanics). No AST/catalog access - the
/// visitor still owns recognizing the shape (a function call wrapping a column) and resolving the
/// column's own catalog facts; this only decides what those facts mean once resolved.
/// </summary>
public static class SargabilityClassifier
{
    /// <summary>
    /// Named date-form rule (docs/detection-checklist.md Tier 1 "Type-aware upgrade of the
    /// sargability stream" #2) - oracle-verified structurally identical to case-folding: always
    /// forces a scan, no type/verdict question, only the computed-column precision guard can
    /// suppress it.
    /// </summary>
    private static readonly HashSet<string> DateFunctionNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "YEAR", "MONTH", "DAY", "DATEPART", "DATEDIFF", "DATEADD", "DATENAME",
    };

    public static bool IsDateFunction(string functionName) => DateFunctionNames.Contains(functionName);

    /// <summary>
    /// Calling ISNULL with a NOT NULL column as the first argument and a default value as the
    /// second is a false positive the blanket function-wrap rule otherwise cannot catch (docs/
    /// detection-checklist.md Tier 1 "Type-aware upgrade of the sargability stream" #1).
    /// Confirmed directly against the standing oracle that the optimizer proves that call is
    /// equivalent to the bare column and simplifies the wrap away entirely, regardless of the
    /// default argument's own type. Even a widening default value still seeks, so this is purely
    /// a nullability fact and never a type question. COALESCE gets no equivalent suppression -
    /// confirmed separately against the same oracle that the identical call shape written with
    /// COALESCE instead still scans even with no type conversion at all, since COALESCE is CASE
    /// syntax sugar and the optimizer never folds it the way it folds ISNULL. Takes the column's
    /// already-resolved not-null fact rather than a column reference - catalog resolution stays
    /// the caller's own concern, this only decides what the resolved fact means.
    /// </summary>
    public static bool ShouldSuppressIsNullOnKnownNotNullColumn(string functionName, bool columnIsKnownNotNull) =>
        string.Equals(functionName, "ISNULL", StringComparison.OrdinalIgnoreCase) && columnIsKnownNotNull;

    /// <summary>
    /// <c>CHARINDEX(x, col) = 1</c> is exactly equivalent to <c>col LIKE 'x%'</c> - a real,
    /// always-usable sargable rewrite. Any other comparison against CHARINDEX still wraps the
    /// column (still non-sargable, still reported) but has no such rewrite - a genuine substring
    /// search. Takes the already-decided "is this the exact prefix-match shape" fact rather than
    /// the raw comparison/literal - recognizing that shape from the AST stays the caller's own
    /// concern.
    /// </summary>
    public static string DescribeCharindexRemediation(bool isExactPrefixMatch) => isExactPrefixMatch
        ? "CHARINDEX(x, col) = 1 is a prefix match - rewritable to col LIKE 'x%', which restores the seek."
        : "CHARINDEX(x, col) is a substring search - no sargable rewrite exists (unlike the = 1 prefix-match case).";

    /// <summary>
    /// <c>LEFT(col, n) = 'x'</c> (with <c>LEN('x') == n</c>) is exactly equivalent to
    /// <c>col LIKE 'x%'</c> - the same rewrite <see cref="DescribeCharindexRemediation"/> reports
    /// for CHARINDEX's own identical shape.
    /// </summary>
    public static string DescribeLeftRemediation(bool isExactPrefixMatch) => isExactPrefixMatch
        ? "LEFT(col, n) = 'x' (LEN('x') = n) is a prefix match - rewritable to col LIKE 'x%', which restores the seek."
        : "LEFT(col, n) wraps the column - no sargable rewrite applies unless the compared literal's own length exactly matches n.";

    public static bool IsCaseFoldFunction(string functionName) =>
        string.Equals(functionName, "UPPER", StringComparison.OrdinalIgnoreCase)
        || string.Equals(functionName, "LOWER", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// UPPER/LOWER get their own finding kind and their own remediation text, not the generic
    /// FunctionWrappedColumn one - oracle-verified (docs/detection-checklist.md Tier 1 "Type-aware
    /// upgrade of the sargability stream" #4) that the wrap forces a scan under EITHER collation
    /// family, so it's never suppressed by collation the way the checklist originally assumed;
    /// only the remediation advice changes: a case-insensitive column's wrap is a provably safe
    /// no-op to delete (the result set is identical either way), a case-sensitive/binary column's
    /// wrap is load-bearing and needs a real rewrite.
    /// </summary>
    public static string DescribeCaseFoldRemediation(string functionName, Collation? collation) => collation switch
    {
        null => $"{functionName} wraps the column, forcing a scan - collation unresolved, cannot confirm whether the wrap is provably redundant.",
        { IsCaseSensitive: true } => $"{functionName} wraps the column, forcing a scan, and the column's collation ({collation.Name}) is case-sensitive - the wrap is load-bearing for correctness; rewrite via an indexed computed column or a case-insensitive COLLATE on the literal instead of the column.",
        _ => $"{functionName} wraps the column, forcing a scan, but the column's collation ({collation.Name}) is already case-insensitive - the wrap changes nothing about which rows match and can be deleted with zero result-set risk.",
    };
}
