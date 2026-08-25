using Microsoft.SqlServer.TransactSql.ScriptDom;
using SilentScan.Core.Catalog;
using SilentScan.Core.Parsing;

namespace SilentScan.Core.Predicates;

internal static class ComputedColumnMatcher
{
    public static bool HasIndexedMatchingComputedColumn(
        DatabaseCatalog catalog, string tableQualifiedName, ScalarExpression predicateExpression)
    {
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

            var definitionExpression = TryParseTopLevelExpression(expression.DefinitionText);
            if (definitionExpression is not null && StructurallyEqual(definitionExpression, predicateExpression))
            {
                return true;
            }
        }

        return false;
    }

    private static ScalarExpression? TryParseTopLevelExpression(string definitionText)
    {
        var result = SqlScriptParser.ParseText("schema-expression.sql", $"SELECT {definitionText};");
        if (result.HasErrors || result.Fragment is not TSqlScript { Batches: [{ Statements: [SelectStatement { QueryExpression: QuerySpecification { SelectElements: [SelectScalarExpression selectScalar] } } ] }] })
        {
            return null;
        }

        return Unwrap(selectScalar.Expression);
    }

    private static ScalarExpression Unwrap(ScalarExpression expression)
    {
        while (expression is ParenthesisExpression parenthesized)
        {
            expression = parenthesized.Expression;
        }

        return expression;
    }

    private static bool StructurallyEqual(ScalarExpression? a, ScalarExpression? b)
    {
        a = a is null ? null : Unwrap(a);
        b = b is null ? null : Unwrap(b);

        if (TryAsCanonicalDatePart(a) is { } canonicalA)
        {
            a = canonicalA;
        }

        if (TryAsCanonicalDatePart(b) is { } canonicalB)
        {
            b = canonicalB;
        }

        return (a, b) switch
        {
            (null, null) => true,
            (ColumnReferenceExpression ca, ColumnReferenceExpression cb) => string.Equals(LastIdentifier(ca), LastIdentifier(cb), StringComparison.OrdinalIgnoreCase),
            (FunctionCall fa, FunctionCall fb) => string.Equals(fa.FunctionName.Value, fb.FunctionName.Value, StringComparison.OrdinalIgnoreCase)
                && fa.Parameters.Count == fb.Parameters.Count
                && fa.Parameters.Zip(fb.Parameters, StructurallyEqual).All(equal => equal),

            (LeftFunctionCall la, LeftFunctionCall lb) => la.Parameters.Count == lb.Parameters.Count
                && la.Parameters.Zip(lb.Parameters, StructurallyEqual).All(equal => equal),
            (CastCall casta, CastCall castb) => TypeEqual(casta.DataType, castb.DataType) && StructurallyEqual(casta.Parameter, castb.Parameter),
            (ConvertCall converta, ConvertCall convertb) => TypeEqual(converta.DataType, convertb.DataType)
                && StructurallyEqual(converta.Parameter, convertb.Parameter)
                && StructurallyEqual(converta.Style, convertb.Style),
            (StringLiteral sa, StringLiteral sb) => string.Equals(sa.Value, sb.Value, StringComparison.Ordinal),
            (IntegerLiteral ia, IntegerLiteral ib) => string.Equals(ia.Value, ib.Value, StringComparison.Ordinal),
            (IdentifierLiteral da, IdentifierLiteral db) => string.Equals(da.Value, db.Value, StringComparison.OrdinalIgnoreCase),
            _ => false,
        };
    }

    private static readonly HashSet<string> DatePartSugarFunctions = new(StringComparer.OrdinalIgnoreCase) { "YEAR", "MONTH", "DAY" };

    private static FunctionCall? TryAsCanonicalDatePart(ScalarExpression? expression) =>
        expression is FunctionCall { Parameters.Count: 1 } call && DatePartSugarFunctions.Contains(call.FunctionName.Value)
            ? new FunctionCall
            {
                FunctionName = new Identifier { Value = "DATEPART" },
                Parameters = { new IdentifierLiteral { Value = call.FunctionName.Value }, call.Parameters[0] },
            }
            : null;

    private static bool TypeEqual(DataTypeReference a, DataTypeReference b) =>
        SqlTypeReferenceResolver.Resolve(a, columnCollation: null) is { } resolvedA
        && SqlTypeReferenceResolver.Resolve(b, columnCollation: null) is { } resolvedB
        && resolvedA == resolvedB;

    private static string? LastIdentifier(ColumnReferenceExpression columnRef) =>
        columnRef.MultiPartIdentifier?.Identifiers is { Count: > 0 } identifiers ? identifiers[^1].Value : null;
}
