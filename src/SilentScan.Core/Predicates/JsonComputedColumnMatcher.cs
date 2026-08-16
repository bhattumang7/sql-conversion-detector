using Microsoft.SqlServer.TransactSql.ScriptDom;
using SilentScan.Core.Catalog;
using SilentScan.Core.Parsing;

namespace SilentScan.Core.Predicates;

/// <summary>
/// Corrections-to-shipped-work: since SQL Server 2016, <c>JSON_VALUE(col, '$.path')</c> (and,
/// less usefully in practice, <c>JSON_QUERY</c>) can match a computed column whose definition is
/// the identical expression, and the optimizer seeks on that computed column's index exactly as
/// if the query had named it directly - so the blanket "function call wraps the column → lost
/// seek" read the shipped function-wrapped-column rule applies to every other function is wrong
/// here specifically. Oracle-verified against the Docker instance (SQL Server 2022, compat 160):
/// an exact-match indexed computed column produces a genuine Index Seek with the JSON_VALUE
/// Intrinsic gone from the plan entirely; a similar-but-different JSON path, or the same path
/// under a CAST the predicate doesn't also use, still scans - so matching here is deliberately
/// exact-AST, never fuzzy/substring, to avoid trading a false positive for a false suppression.
/// Engine-version note: this suppression only applies from SQL Server 2016 onward (the release
/// JSON_VALUE itself shipped in), which is moot for the fires case - a target too old to have
/// JSON_VALUE at all can't produce the predicate this class is asked to suppress in the first
/// place.
/// </summary>
internal static class JsonComputedColumnMatcher
{
    private static readonly HashSet<string> JsonPathFunctionNames =
        new(StringComparer.OrdinalIgnoreCase) { "JSON_VALUE", "JSON_QUERY" };

    public static bool IsJsonPathFunction(string functionName) => JsonPathFunctionNames.Contains(functionName);

    /// <summary>
    /// True when <paramref name="catalog"/> has an indexed computed column on
    /// <paramref name="tableQualifiedName"/> whose own definition is the exact same
    /// <c>JSON_VALUE</c>/<c>JSON_QUERY</c> call (same function, same source column, same literal
    /// path string) as <paramref name="predicateCall"/> - the one shape the engine can actually
    /// substitute and seek on.
    /// </summary>
    public static bool HasIndexedMatchingComputedColumn(
        DatabaseCatalog catalog, string tableQualifiedName, string sourceColumnName, FunctionCall predicateCall)
    {
        if (!TryGetJsonPathLiteral(predicateCall, sourceColumnName, out var predicatePath))
        {
            return false;
        }

        var table = catalog.Find(tableQualifiedName);
        if (table is null)
        {
            return false;
        }

        foreach (var expression in catalog.SchemaExpressions)
        {
            if (expression.Kind != SchemaDependencyKind.ComputedColumn
                || expression.ColumnName is not { } computedColumnName
                || !string.Equals(expression.TableQualifiedName, tableQualifiedName, StringComparison.OrdinalIgnoreCase)
                || !table.IsIndexedColumn(computedColumnName))
            {
                continue;
            }

            var definitionCall = TryParseTopLevelFunctionCall(expression.DefinitionText);
            if (definitionCall is null
                || !TryGetJsonPathLiteral(definitionCall, sourceColumnName, out var definitionPath))
            {
                continue;
            }

            if (string.Equals(definitionCall.FunctionName.Value, predicateCall.FunctionName.Value, StringComparison.OrdinalIgnoreCase)
                // JSON path property names are case-sensitive at the engine level, so the literal
                // path text is compared ordinally - never guess a suppression from a
                // case-insensitive near-match.
                && string.Equals(definitionPath, predicatePath, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// A JSON path function call over exactly the named source column, with a literal (not
    /// parameter/expression) path string - the only shape a computed column's own definition can
    /// ever be, and the only shape this rule can safely compare without guessing.
    /// </summary>
    private static bool TryGetJsonPathLiteral(FunctionCall call, string sourceColumnName, out string path)
    {
        path = string.Empty;

        if (call.Parameters is not [ColumnReferenceExpression columnRef, StringLiteral pathLiteral])
        {
            return false;
        }

        var columnName = columnRef.MultiPartIdentifier?.Identifiers is { Count: > 0 } identifiers
            ? identifiers[^1].Value
            : null;

        if (!string.Equals(columnName, sourceColumnName, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        path = pathLiteral.Value;
        return true;
    }

    /// <summary>
    /// Reparses a computed column's raw definition text - the only shape live mode's
    /// <c>sys.computed_columns.definition</c> can ever produce - the same throwaway-wrapper-
    /// statement trick <see cref="SchemaDependencyScanner"/> already uses for the identical
    /// text-not-AST problem.
    /// </summary>
    private static FunctionCall? TryParseTopLevelFunctionCall(string definitionText)
    {
        var result = SqlScriptParser.ParseText("schema-expression.sql", $"SELECT {definitionText};");
        if (result.HasErrors || result.Fragment is not TSqlScript { Batches: [{ Statements: [SelectStatement { QueryExpression: QuerySpecification { SelectElements: [SelectScalarExpression selectScalar] } } ] }] })
        {
            return null;
        }

        var expression = selectScalar.Expression;
        while (expression is ParenthesisExpression parenthesized)
        {
            expression = parenthesized.Expression;
        }

        return expression as FunctionCall;
    }
}
