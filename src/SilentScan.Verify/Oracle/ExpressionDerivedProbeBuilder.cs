using SilentScan.Core.Predicates;

namespace SilentScan.Verify.Oracle;

public static class ExpressionDerivedProbeBuilder
{
public static string? Build(ExpressionDerivedFinding finding)
    {
        if (finding.PredicateFragmentText is not { } text || finding.ImmediateRelationQualifiedName is not { } relation)
        {
            return null;
        }

        var table = BracketQualifiedName(relation);
        var fromClause = finding.ImmediateRelationAlias is { } alias
            ? $"{table} AS {Bracket(alias)}"
            : table;

        return $"SELECT 1 FROM {fromClause} WHERE {text};";
    }

    private static string BracketQualifiedName(string qualifiedName)
    {
        var parts = qualifiedName.Split('.', 2);
        return parts.Length == 2 ? $"{Bracket(parts[0])}.{Bracket(parts[1])}" : Bracket(parts[0]);
    }

    private static string Bracket(string identifier) => $"[{identifier.Replace("]", "]]", StringComparison.Ordinal)}]";
}
