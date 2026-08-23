using SilentScan.Core.Predicates;

namespace SilentScan.Verify.Oracle;

public static class CollationConflictProbeBuilder
{
    public static string Build(CollationConflictFinding finding)
    {
        var table1 = BracketQualifiedName(finding.FirstTableQualifiedName);
        var column1 = Bracket(finding.FirstColumnName);
        var table2 = BracketQualifiedName(finding.SecondTableQualifiedName);
        var column2 = Bracket(finding.SecondColumnName);

        return $"SELECT 1 FROM {table1} AS t1 CROSS JOIN {table2} AS t2 WHERE t1.{column1} {finding.Operator} t2.{column2};";
    }

    private static string BracketQualifiedName(string qualifiedName)
    {
        var parts = qualifiedName.Split('.', 2);
        return parts.Length == 2 ? $"{Bracket(parts[0])}.{Bracket(parts[1])}" : Bracket(parts[0]);
    }

    private static string Bracket(string identifier) => $"[{identifier.Replace("]", "]]", StringComparison.Ordinal)}]";
}
