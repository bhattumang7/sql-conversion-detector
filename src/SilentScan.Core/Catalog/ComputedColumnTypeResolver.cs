using Microsoft.SqlServer.TransactSql.ScriptDom;
using SilentScan.Core.Parsing;
using SilentScan.Core.Rules;

namespace SilentScan.Core.Catalog;

/// <summary>
/// Infers a computed column's type from its defining expression (<c>Total AS (Price * Qty)</c>)
/// - previously never attempted, so every computed column stayed Unknown forever regardless of
/// how trivially inferable its expression was. Sibling column references, literals, CAST/
/// CONVERT, arithmetic, and (via the shared <see cref="Rules.ExpressionTypeInferencer"/>, roadmap
/// Phase B) CASE/COALESCE/NULLIF/IIF are all resolved; an ordinary function call still resolves
/// null (Unknown) here - a scalar UDF's return-type registry isn't built yet at this point in
/// CatalogBuilder's pass ordering, and this pass never guesses.
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

    /// <summary>
    /// Delegates to the shared <see cref="Rules.ExpressionTypeInferencer"/> (roadmap Phase B) for
    /// every expression shape it owns (arithmetic, CASE/COALESCE/NULLIF/IIF, CAST/CONVERT,
    /// parenthesis/unary) - the leaf callback resolves only a bare sibling-column reference,
    /// which is all a computed column's own expression can legitimately contain beyond those
    /// shapes (no catalog/scope machinery exists at this point in CatalogBuilder's pass
    /// ordering for anything richer, e.g. a function call needing the scalar-UDF registry).
    /// </summary>
    private static SqlType? Resolve(
        ScalarExpression expression, IReadOnlyDictionary<string, SqlType?> columnTypes, IReadOnlyDictionary<string, SqlType>? typeAliases) =>
        Rules.ExpressionTypeInferencer.Resolve(expression, e => ResolveLeaf(e, columnTypes), typeAliases);

    private static SqlType? ResolveLeaf(ScalarExpression expression, IReadOnlyDictionary<string, SqlType?> columnTypes) => expression switch
    {
        ColumnReferenceExpression { MultiPartIdentifier.Identifiers: [.., { } last] } =>
            columnTypes.GetValueOrDefault(last.Value),

        _ => null,
    };
}
