using SilentScan.Core.Predicates;

namespace SilentScan.Verify.Oracle;

/// <summary>
/// Builds a self-authored, compile-only probe for a <see cref="CollationConflictFinding"/> -
/// a plain cross join comparing the two real, already-deployed columns directly, borrowing only
/// their table/column names from the corpus, never its logic. Both columns' own DDL-declared
/// collations do the rest; unlike <see cref="CorpusFindingProbeBuilder"/> there is no operand
/// type to reconstruct or COLLATE clause to add, since a collation conflict finding is always a
/// direct column-vs-column comparison by its own definition (never a value/literal, which is
/// always coercible and can never conflict).
/// </summary>
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
