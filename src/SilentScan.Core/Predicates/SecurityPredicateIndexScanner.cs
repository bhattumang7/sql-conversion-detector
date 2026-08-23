using Microsoft.SqlServer.TransactSql.ScriptDom;
using SilentScan.Core.Catalog;
using SilentScan.Core.Parsing;
using SilentScan.Core.Common;

namespace SilentScan.Core.Predicates;

public static class SecurityPredicateIndexScanner
{
    public static IReadOnlyList<SecurityPredicateIndexFinding> Scan(DatabaseCatalog catalog)
    {
        var findings = new List<SecurityPredicateIndexFinding>();

        foreach (var predicate in catalog.SecurityPredicates)
        {
            AnalyzeSecurityPredicate(catalog, predicate, findings);
        }

        return
        [
            .. findings
                .OrderBy(f => f.TableQualifiedName, StringComparer.Ordinal)
                .ThenBy(f => f.PolicyQualifiedName, StringComparer.Ordinal),
        ];
    }

    private static void AnalyzeSecurityPredicate(
        DatabaseCatalog catalog, CatalogSecurityPredicate predicate, List<SecurityPredicateIndexFinding> findings)
    {
        if (!predicate.IsPolicyEnabled || !predicate.IsFilterPredicate
            || string.IsNullOrWhiteSpace(predicate.PredicateDefinitionText))
        {
            return;
        }

        var table = catalog.Find(predicate.TargetTableQualifiedName);
        if (table is null)
        {
            return;
        }

        var call = TryParseFunctionCall(predicate.PredicateDefinitionText);
        if (call is null)
        {
            return;
        }

        var boundColumns = call.Parameters
            .OfType<ColumnReferenceExpression>()
            .Select(c => c.MultiPartIdentifier?.Identifiers is { Count: > 0 } ids ? ids[^1].Value : null)
            .Where(name => name is not null)
            .Select(name => name!)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (boundColumns.Count == 0)
        {
            return;
        }

        var hasSupportingIndex = table.Indexes.Any(i =>
            !i.IsDisabled && !i.IsFiltered && !i.IsColumnstore && i.KeyColumns.Count > 0
            && boundColumns.Contains(i.KeyColumns[0], StringComparer.OrdinalIgnoreCase));

        if (hasSupportingIndex)
        {
            return;
        }

        findings.Add(new SecurityPredicateIndexFinding(
            predicate.PolicyQualifiedName,
            predicate.TargetTableQualifiedName,
            SchemaObjectNameHelper.QualifyFunctionCall(call),
            boundColumns,
            table.SourcePath,
            table.SourceLine));
    }

private static FunctionCall? TryParseFunctionCall(string definitionText)
    {
        var result = SqlScriptParser.ParseText("security-predicate.sql", $"SELECT {definitionText};");
        if (result.HasErrors
            || result.Fragment is not TSqlScript { Batches: [{ Statements: [SelectStatement { QueryExpression: QuerySpecification { SelectElements: [SelectScalarExpression selectScalar] } }] }] })
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
