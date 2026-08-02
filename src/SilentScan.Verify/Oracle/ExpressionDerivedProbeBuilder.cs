using SilentScan.Core.Predicates;

namespace SilentScan.Verify.Oracle;

/// <summary>
/// Roadmap Phase E3: builds a self-authored, compile-only probe for an
/// <see cref="ExpressionDerivedFinding"/> - previously unprobeable at all, since the finding
/// carried only a human-readable transformation-chain description, never the actual predicate
/// or which real object it was written against. Queries the finding's own
/// <see cref="ExpressionDerivedFinding.ImmediateRelationQualifiedName"/> directly (the view/TVF
/// the predicate was actually written against, exactly like <see cref="CorpusFindingProbeBuilder"/>
/// does for a depth&gt;=1 typed finding) - the optimizer inlines the view, so the resulting plan
/// still reflects the real underlying base column's own index.
/// </summary>
public static class ExpressionDerivedProbeBuilder
{
    /// <summary>Returns the probe SQL for <paramref name="finding"/>, or null when it lacks enough information (no rendered predicate, or the column came from an inline derived table/CTE with no standalone queryable object to target) - never guessed.</summary>
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
