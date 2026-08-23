using Microsoft.SqlServer.TransactSql.ScriptDom;
using SilentScan.Core.Catalog;
using SilentScan.Core.Parsing;

namespace SilentScan.Core.Predicates;

internal static class JsonComputedColumnMatcher
{
    private static readonly HashSet<string> JsonPathFunctionNames =
        new(StringComparer.OrdinalIgnoreCase) { "JSON_VALUE", "JSON_QUERY" };

    public static bool IsJsonPathFunction(string functionName) => JsonPathFunctionNames.Contains(functionName);

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
                && string.Equals(definitionPath, predicatePath, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

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
