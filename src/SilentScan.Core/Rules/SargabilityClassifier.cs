using SilentScan.Core.Catalog;
using SilentScan.Core.TypeInference;

namespace SilentScan.Core.Rules;

public static class SargabilityClassifier
{
private static readonly HashSet<string> DateFunctionNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "YEAR", "MONTH", "DAY", "DATEPART", "DATEDIFF", "DATEADD", "DATENAME",
    };

    public static bool IsDateFunction(string functionName) => DateFunctionNames.Contains(functionName);

public static bool ShouldSuppressIsNullOnKnownNotNullColumn(string functionName, bool columnIsKnownNotNull) =>
        string.Equals(functionName, "ISNULL", StringComparison.OrdinalIgnoreCase) && columnIsKnownNotNull;

public static string DescribeCharindexRemediation(bool isExactPrefixMatch) => isExactPrefixMatch
        ? "CHARINDEX(x, col) = 1 is a prefix match - rewritable to col LIKE 'x%', which restores the seek."
        : "CHARINDEX(x, col) is a substring search - no sargable rewrite exists (unlike the = 1 prefix-match case).";

public static string DescribeLeftRemediation(bool isExactPrefixMatch) => isExactPrefixMatch
        ? "LEFT(col, n) = 'x' (LEN('x') = n) is a prefix match - rewritable to col LIKE 'x%', which restores the seek."
        : "LEFT(col, n) wraps the column - no sargable rewrite applies unless the compared literal's own length exactly matches n.";

    public static bool IsCaseFoldFunction(string functionName) =>
        string.Equals(functionName, "UPPER", StringComparison.OrdinalIgnoreCase)
        || string.Equals(functionName, "LOWER", StringComparison.OrdinalIgnoreCase);

public static string DescribeCaseFoldRemediation(string functionName, Collation? collation) => collation switch
    {
        null => $"{functionName} wraps the column, forcing a scan - collation unresolved, cannot confirm whether the wrap is provably redundant.",
        { IsCaseSensitive: true } => $"{functionName} wraps the column, forcing a scan, and the column's collation ({collation.Name}) is case-sensitive - the wrap is load-bearing for correctness; rewrite via an indexed computed column or a case-insensitive COLLATE on the literal instead of the column.",
        _ => $"{functionName} wraps the column, forcing a scan, but the column's collation ({collation.Name}) is already case-insensitive - the wrap changes nothing about which rows match and can be deleted with zero result-set risk.",
    };
}
