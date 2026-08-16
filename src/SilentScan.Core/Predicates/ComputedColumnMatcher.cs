using Microsoft.SqlServer.TransactSql.ScriptDom;
using SilentScan.Core.Catalog;
using SilentScan.Core.Parsing;

namespace SilentScan.Core.Predicates;

/// <summary>
/// Generalization of the mandatory precision guard the shipped
/// <see cref="JsonComputedColumnMatcher"/> established for JSON_VALUE/JSON_QUERY: SQL Server can
/// match ANY expression - not just a JSON path function - to an indexed computed column whose
/// definition is the identical expression, and seek on it instead of scanning
/// (docs/detection-checklist.md Tier 1 "Type-aware upgrade of the sargability stream": "a
/// function wrapping a column does not imply a lost seek when an indexed computed column matches
/// the same expression"). Where <see cref="JsonComputedColumnMatcher"/> hand-parses the narrow,
/// fixed <c>[column, string-literal]</c> shape every JSON path call has, this class does genuine
/// structural equality over an arbitrary <see cref="ScalarExpression"/> subtree - the shape every
/// OTHER function-wrapped-column rule in this stream needs (<c>YEAR(col)</c>,
/// <c>DATEDIFF(day, col, x)</c>, <c>UPPER(col)</c>, <c>CONVERT(varchar, col, 112)</c>, ...).
/// Deliberately NOT used to refactor the already-shipped, already-tested
/// <see cref="JsonComputedColumnMatcher"/> - that class stays as-is to avoid regressing working,
/// oracle-verified code for a refactor with no new behavior to show for it.
/// </summary>
internal static class ComputedColumnMatcher
{
    /// <summary>
    /// True when <paramref name="catalog"/> has an indexed computed column on
    /// <paramref name="tableQualifiedName"/> whose own definition is structurally identical to
    /// <paramref name="predicateExpression"/> - the one shape the engine can actually substitute
    /// and seek on. Exact match only, never fuzzy: a similar-but-different expression (a
    /// different DATEPART unit, a different CONVERT style, a different literal) must not
    /// wrongly suppress a real finding.
    /// </summary>
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

    /// <summary>
    /// Reparses a computed column's raw definition text via the same throwaway-wrapper-statement
    /// trick <see cref="SchemaDependencyScanner"/>/<see cref="JsonComputedColumnMatcher"/> both
    /// already use - the only shape live mode's <c>sys.computed_columns.definition</c> can ever
    /// produce.
    /// </summary>
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

    /// <summary>
    /// Recursive structural equality over the small set of scalar-expression node shapes the
    /// date-function/case-fold rules actually need to compare - column references (last
    /// identifier, case-insensitive - matching how T-SQL identifiers themselves resolve),
    /// function calls (name + every parameter, recursively, in order), CAST/CONVERT (target
    /// type + parameter, and CONVERT's style argument), string/integer literals (value,
    /// ordinal), and the bare identifier ScriptDOM uses for a DATEPART/DATEDIFF/DATEADD "unit"
    /// argument (<c>day</c>, <c>year</c>, ... - a T-SQL keyword, not a real identifier, so
    /// compared case-insensitively). Any other node shape is never equal (never a guess) - a
    /// wrap this comparer doesn't recognize simply never suppresses.
    /// </summary>
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
            // LEFT gets its own dedicated ScriptDOM node type (not a generic FunctionCall) -
            // oracle-verified sys.computed_columns.definition stores it unnormalized as
            // left([col],(n)), so a plain parameter-list comparison (same shape the generic
            // FunctionCall case already uses) is correct with no canonicalization needed.
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

    /// <summary>
    /// YEAR(x)/MONTH(x)/DAY(x) are pure syntactic sugar for DATEPART(year/month/day, x) -
    /// oracle-verified directly (Docker instance): SQL Server rewrites a computed column defined
    /// as <c>YEAR(OrderDate)</c> to <c>datepart(year,[OrderDate])</c> the moment it's stored in
    /// <c>sys.computed_columns.definition</c>, so a predicate written as the original YEAR(...)
    /// form would never structurally match the stored DATEPART(...) form without this
    /// normalization - a real false-negative this guard exists specifically to prevent (the
    /// exact opposite failure mode from a wrong suppression: silently NEVER suppressing a
    /// genuinely matching computed column because of a cosmetic rewrite). Canonicalizes ONLY the
    /// three date-part synonyms actually confirmed to rewrite this way; MONTH/DAY confirmed
    /// alongside YEAR in the same probe.
    /// </summary>
    private static FunctionCall? TryAsCanonicalDatePart(ScalarExpression? expression) =>
        expression is FunctionCall { Parameters.Count: 1 } call && DatePartSugarFunctions.Contains(call.FunctionName.Value)
            ? new FunctionCall
            {
                FunctionName = new Identifier { Value = "DATEPART" },
                Parameters = { new IdentifierLiteral { Value = call.FunctionName.Value }, call.Parameters[0] },
            }
            : null;

    private static bool TypeEqual(DataTypeReference a, DataTypeReference b) =>
        a is SqlDataTypeReference sqlA && b is SqlDataTypeReference sqlB && sqlA.SqlDataTypeOption == sqlB.SqlDataTypeOption;

    private static string? LastIdentifier(ColumnReferenceExpression columnRef) =>
        columnRef.MultiPartIdentifier?.Identifiers is { Count: > 0 } identifiers ? identifiers[^1].Value : null;
}
