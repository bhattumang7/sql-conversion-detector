using SilentScan.Core.Predicates;

namespace SilentScan.Verify.Oracle;

/// <summary>
/// Builds a self-authored, compile-only probe statement for a <see cref="TypedPredicateFinding"/>
/// against the corpus repo's own deployed DDL (CLAUDE.md Verify: "for each SCAN_FORCED
/// finding, execute a parameterized probe of the predicate and confirm CONVERT_IMPLICIT-on-
/// column"). The probe never runs the repo's own SQL - it reconstructs an equivalent minimal
/// comparison from the finding's resolved column and operand types, so only tables/columns
/// are borrowed from the corpus, never its logic.
/// </summary>
public static class CorpusFindingProbeBuilder
{
    /// <summary>Returns the probe SQL for <paramref name="finding"/>, or null if the finding lacks enough type information to synthesize one (reported as not-probeable, never guessed).</summary>
    public static string? Build(TypedPredicateFinding finding)
    {
        var table = BracketQualifiedName(finding.Column.TableQualifiedName);
        var column = Bracket(finding.Column.ColumnName);

        return finding.OtherOperand switch
        {
            PredicateOperand.Value { Type: { } valueType } => BuildValueProbe(table, column, finding.Operator, valueType),
            PredicateOperand.Column otherColumn => BuildColumnProbe(table, column, finding.Operator, otherColumn),
            _ => null,
        };
    }

    private static string? BuildValueProbe(string table, string column, string op, Core.Catalog.SqlType valueType)
    {
        var typeSyntax = SqlTypeSyntaxFormatter.Format(valueType);
        if (typeSyntax is null)
        {
            return null;
        }

        return $"""
            DECLARE @p {typeSyntax};
            SELECT 1 FROM {table} WHERE {column} {op} @p;
            """;
    }

    private static string? BuildColumnProbe(string table, string column, string op, PredicateOperand.Column otherColumn)
    {
        var otherTable = BracketQualifiedName(otherColumn.TableQualifiedName);
        var otherColumnName = Bracket(otherColumn.ColumnName);

        return $"SELECT 1 FROM {table} AS t1 CROSS JOIN {otherTable} AS t2 WHERE t1.{column} {op} t2.{otherColumnName};";
    }

    private static string BracketQualifiedName(string qualifiedName)
    {
        var parts = qualifiedName.Split('.', 2);
        return parts.Length == 2 ? $"{Bracket(parts[0])}.{Bracket(parts[1])}" : Bracket(parts[0]);
    }

    private static string Bracket(string identifier) => $"[{identifier.Replace("]", "]]", StringComparison.Ordinal)}]";
}
