using SilentScan.Core.Catalog;
using SilentScan.Core.Predicates;

namespace SilentScan.Verify.Oracle;

/// <summary>
/// Roadmap Phase E3: builds a self-authored, compile-only probe for a <see cref="SargabilityFinding"/>
/// - previously unprobeable at all, since the finding carried only a description (function name,
/// CAST/CONVERT keyword, arithmetic operator), never the actual predicate text. Now that
/// <see cref="SargabilityFinding.PredicateFragmentText"/> carries the real fragment the tool's
/// Tier-1 syntactic scanner matched on, this reconstructs it exactly rather than guessing a
/// synthetic stand-in.
/// </summary>
public static class Tier1ProbeBuilder
{
    /// <summary>Returns the probe SQL for <paramref name="finding"/>, or null when it lacks enough information to synthesize one (no rendered fragment, no resolved table, or - for the two kinds that need a synthesized comparison - no resolvable column type) - never guessed.</summary>
    public static string? Build(SargabilityFinding finding, DatabaseCatalog catalog)
    {
        if (finding.PredicateFragmentText is not { } fragmentText || finding.TableQualifiedName is not { } tableQualifiedName)
        {
            return null;
        }

        var table = BracketQualifiedName(tableQualifiedName);

        if (finding.Kind is SargabilityFindingKind.LeadingWildcardLike or SargabilityFindingKind.LikePatternNotLiteral)
        {
            // The captured fragment is already the whole LIKE predicate - a complete, probeable
            // boolean expression on its own, nothing left to synthesize.
            return $"SELECT 1 FROM {table} WHERE {fragmentText};";
        }

        // FunctionWrappedColumn/CastOrConvertOnColumn/ColumnArithmetic: the captured fragment is
        // a bare scalar expression (e.g. UPPER(Code)) - compares it against a variable of the
        // WRAPPED COLUMN's own declared type, so the probe compiles without needing to guess
        // what the original predicate's other side actually was.
        var columnType = catalog.Find(tableQualifiedName)?.FindColumn(finding.ColumnName)?.Type;
        if (columnType is null)
        {
            return null;
        }

        var typeSyntax = SqlTypeSyntaxFormatter.Format(columnType);
        if (typeSyntax is null)
        {
            return null;
        }

        var collateClause = SqlTypeSyntaxFormatter.FormatCollateClause(columnType);
        return $"""
            DECLARE @p {typeSyntax};
            SELECT 1 FROM {table} WHERE {fragmentText} = @p{collateClause};
            """;
    }

    private static string BracketQualifiedName(string qualifiedName)
    {
        var parts = qualifiedName.Split('.', 2);
        return parts.Length == 2 ? $"{Bracket(parts[0])}.{Bracket(parts[1])}" : Bracket(parts[0]);
    }

    private static string Bracket(string identifier) => $"[{identifier.Replace("]", "]]", StringComparison.Ordinal)}]";
}
