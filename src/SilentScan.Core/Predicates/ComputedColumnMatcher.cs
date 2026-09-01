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
                || !catalog.IdentifierComparer.Equals(expression.TableQualifiedName, tableQualifiedName)
                || !table.IsIndexedColumn(computedColumnName, catalog.IdentifierComparer))
            {
                continue;
            }

            var definitionExpression = TryParseTopLevelExpression(expression.DefinitionText, catalog.CompatibilityLevel);
            if (definitionExpression is not null && StructurallyEqual(definitionExpression, predicateExpression, catalog.IdentifierComparer))
            {
                return true;
            }
        }

        return false;
    }

    private static ScalarExpression? TryParseTopLevelExpression(string definitionText, int? compatibilityLevel)
    {
        var result = SqlScriptParser.ParseText("schema-expression.sql", $"SELECT {definitionText};", initialQuotedIdentifiers: true, compatibilityLevel);
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

    private static bool StructurallyEqual(ScalarExpression? a, ScalarExpression? b, StringComparer identifierComparer)
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
            (ColumnReferenceExpression ca, ColumnReferenceExpression cb) => identifierComparer.Equals(LastIdentifier(ca), LastIdentifier(cb)),
            (FunctionCall fa, FunctionCall fb) => string.Equals(fa.FunctionName.Value, fb.FunctionName.Value, StringComparison.OrdinalIgnoreCase)
                && fa.Parameters.Count == fb.Parameters.Count
                && fa.Parameters.Zip(fb.Parameters, (x, y) => StructurallyEqual(x, y, identifierComparer)).All(equal => equal),

            (LeftFunctionCall la, LeftFunctionCall lb) => la.Parameters.Count == lb.Parameters.Count
                && la.Parameters.Zip(lb.Parameters, (x, y) => StructurallyEqual(x, y, identifierComparer)).All(equal => equal),
            (CastCall casta, CastCall castb) => TypeEqual(casta.DataType, castb.DataType) && StructurallyEqual(casta.Parameter, castb.Parameter, identifierComparer),
            (ConvertCall converta, ConvertCall convertb) => TypeEqual(converta.DataType, convertb.DataType)
                && StructurallyEqual(converta.Parameter, convertb.Parameter, identifierComparer)
                && StructurallyEqual(converta.Style, convertb.Style, identifierComparer),
            (StringLiteral sa, StringLiteral sb) => string.Equals(sa.Value, sb.Value, StringComparison.Ordinal),
            (IntegerLiteral ia, IntegerLiteral ib) => string.Equals(ia.Value, ib.Value, StringComparison.Ordinal),
            (IdentifierLiteral da, IdentifierLiteral db) => string.Equals(NormalizeDatePartUnit(da.Value), NormalizeDatePartUnit(db.Value), StringComparison.OrdinalIgnoreCase),
            _ => false,
        };
    }

    private static readonly HashSet<string> DatePartSugarFunctions = new(StringComparer.OrdinalIgnoreCase) { "YEAR", "MONTH", "DAY" };

    private static readonly string[][] DatePartUnitSynonymGroups =
    [
        ["year", "yy", "yyyy"],
        ["quarter", "qq", "q"],
        ["month", "mm", "m"],
        ["dayofyear", "dy", "y"],
        ["day", "dd", "d"],
        ["week", "wk", "ww"],
        ["iso_week", "isowk", "isoww"],
        ["weekday", "dw", "w"],
        ["hour", "hh"],
        ["minute", "mi", "n"],
        ["second", "ss", "s"],
        ["millisecond", "ms"],
        ["microsecond", "mcs"],
        ["nanosecond", "ns"],
        ["tzoffset", "tz"],
    ];

    private static readonly Dictionary<string, string> DatePartUnitSynonyms = DatePartUnitSynonymGroups
        .SelectMany(group => group.Select(synonym => (synonym, canonical: group[0])))
        .ToDictionary(pair => pair.synonym, pair => pair.canonical, StringComparer.OrdinalIgnoreCase);

    private static string NormalizeDatePartUnit(string value) =>
        DatePartUnitSynonyms.TryGetValue(value, out var canonical) ? canonical : value;

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
