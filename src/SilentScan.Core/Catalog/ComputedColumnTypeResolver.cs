using Microsoft.SqlServer.TransactSql.ScriptDom;
using SilentScan.Core.Parsing;
using SilentScan.Core.Rules;

namespace SilentScan.Core.Catalog;

/// <summary>
/// Infers a computed column's type from its defining expression (<c>Total AS (Price * Qty)</c>)
/// - previously never attempted, so every computed column stayed Unknown forever regardless of
/// how trivially inferable its expression was. Scoped deliberately narrow: sibling column
/// references, literals, CAST/CONVERT, and binary expressions combined via T-SQL data type
/// precedence (<see cref="SqlTypeCategory"/>'s ordinal IS the precedence rank - see its own
/// doc comment - so combining two operand categories is exactly "higher ordinal wins", the
/// same fact <see cref="Rules.VerdictClassifier"/> relies on). Function calls, CASE, and other
/// expression kinds are explicitly NOT attempted here - those are CLAUDE.md's own named hard
/// cases (CASE/COALESCE result typing) or need catalog data (scalar UDF registry) not yet built
/// at this point in CatalogBuilder's pass ordering - and resolve null (Unknown), never a guess.
/// </summary>
internal static class ComputedColumnTypeResolver
{
    /// <summary>
    /// Resolves up to a fixed point: a computed column's expression may reference another
    /// computed column (itself resolved this same pass), and ScriptDom preserves declaration
    /// order regardless of which sibling is referenced. Bounded by column count so a
    /// self-referential/circular definition (invalid T-SQL, but not this pass's job to reject)
    /// can never loop.
    /// </summary>
    public static List<CatalogColumn> ResolveAll(
        List<CatalogColumn> columns, IReadOnlyDictionary<string, ScalarExpression> computedExpressions, IReadOnlyDictionary<string, SqlType>? typeAliases)
    {
        if (computedExpressions.Count == 0)
        {
            return columns;
        }

        for (var iteration = 0; iteration < computedExpressions.Count; iteration++)
        {
            var typesByName = columns
                .Where(c => c.Type is not null)
                .ToDictionary(c => c.Name, c => c.Type, StringComparer.OrdinalIgnoreCase);

            if (!TryResolveOnePass(columns, computedExpressions, typesByName, typeAliases, out var next))
            {
                break;
            }

            columns = next;
        }

        return columns;
    }

    /// <summary>One fixed-point iteration: resolves every still-untyped computed column whose expression is now resolvable given <paramref name="typesByName"/>. Returns false (and leaves <paramref name="result"/> as the unmodified input) when nothing progressed, so the caller's loop can stop instead of spinning through remaining iterations for no reason.</summary>
    private static bool TryResolveOnePass(
        List<CatalogColumn> columns, IReadOnlyDictionary<string, ScalarExpression> computedExpressions,
        Dictionary<string, SqlType?> typesByName, IReadOnlyDictionary<string, SqlType>? typeAliases, out List<CatalogColumn> result)
    {
        var progressed = false;
        var next = new List<CatalogColumn>(columns.Count);

        foreach (var column in columns)
        {
            if (column.Type is not null || !computedExpressions.TryGetValue(column.Name, out var expression))
            {
                next.Add(column);
                continue;
            }

            var resolved = Resolve(expression, typesByName, typeAliases);
            if (resolved is null)
            {
                next.Add(column);
                continue;
            }

            progressed = true;
            next.Add(column with { Type = resolved });
        }

        result = progressed ? next : columns;
        return progressed;
    }

    private static SqlType? Resolve(
        ScalarExpression expression, IReadOnlyDictionary<string, SqlType?> columnTypes, IReadOnlyDictionary<string, SqlType>? typeAliases) => expression switch
    {
        ColumnReferenceExpression { MultiPartIdentifier.Identifiers: [.., { } last] } =>
            columnTypes.GetValueOrDefault(last.Value),

        Literal literal => LiteralTypeResolver.Resolve(literal),

        ParenthesisExpression parenthesis => Resolve(parenthesis.Expression, columnTypes, typeAliases),

        UnaryExpression unary => Resolve(unary.Expression, columnTypes, typeAliases),

        CastCall castCall => SqlTypeReferenceResolver.Resolve(castCall.DataType, columnCollation: null, typeAliases),

        ConvertCall convertCall => SqlTypeReferenceResolver.Resolve(convertCall.DataType, columnCollation: null, typeAliases),

        BinaryExpression binary => Combine(
            Resolve(binary.FirstExpression, columnTypes, typeAliases),
            Resolve(binary.SecondExpression, columnTypes, typeAliases)),

        _ => null,
    };

    /// <summary>
    /// T-SQL data type precedence for a binary operator's result: the LOWER-precedence operand
    /// converts to the higher one's category (the same direction <see cref="SqlTypeCategory"/>'s
    /// ordinal already encodes). Same category with differing, both-resolved string collations
    /// is left null (Unknown) rather than guessed - the identical coercibility gap
    /// <see cref="Rules.VerdictClassifier.ClassifySameCategory"/> already declines to resolve.
    /// </summary>
    private static SqlType? Combine(SqlType? left, SqlType? right)
    {
        if (left is null || right is null)
        {
            return null;
        }

        if (left.Category == right.Category)
        {
            if (!left.IsStringFamily)
            {
                return left;
            }

            if (left.Collation is null)
            {
                return right;
            }

            if (right.Collation is null || left.Collation.Name == right.Collation.Name)
            {
                return left;
            }

            return null;
        }

        var winner = left.Category > right.Category ? left : right;
        return winner.IsStringFamily ? new SqlType(winner.Category, Collation: winner.Collation) : new SqlType(winner.Category);
    }
}
