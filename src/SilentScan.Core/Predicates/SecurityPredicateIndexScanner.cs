using Microsoft.SqlServer.TransactSql.ScriptDom;
using SilentScan.Core.Catalog;
using SilentScan.Core.Parsing;

namespace SilentScan.Core.Predicates;

/// <summary>
/// Catalog-plus-text pass over every live-read, enabled RLS FILTER predicate binding
/// (docs/detection-checklist.md practitioner-sweep item "Row-Level Security predicate function with
/// no supporting index on its own filtered columns") - see <see cref="SecurityPredicateIndexFinding"/>
/// for the full scope/precision story and oracle evidence. Mirrors <see cref="CheckConstraintScanner"/>'s
/// own shape: a catalog-only entry point invoked once per scan, always empty in file mode since <see
/// cref="DatabaseCatalog.SecurityPredicates"/> is only ever populated by <c>LiveCatalogReader</c>.
/// </summary>
public static class SecurityPredicateIndexScanner
{
    public static IReadOnlyList<SecurityPredicateIndexFinding> Scan(DatabaseCatalog catalog)
    {
        var findings = new List<SecurityPredicateIndexFinding>();

        foreach (var predicate in catalog.SecurityPredicates)
        {
            if (!predicate.IsPolicyEnabled || !predicate.IsFilterPredicate
                || string.IsNullOrWhiteSpace(predicate.PredicateDefinitionText))
            {
                continue;
            }

            var table = catalog.Find(predicate.TargetTableQualifiedName);
            if (table is null)
            {
                continue;
            }

            var call = TryParseFunctionCall(predicate.PredicateDefinitionText);
            if (call is null)
            {
                continue;
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
                // The predicate function was invoked with no resolvable bare-column argument (a
                // literal, an expression, or a zero-argument call) - nothing this pass can check
                // against the table's own indexes, so left unanalyzed rather than guessed at.
                continue;
            }

            var hasSupportingIndex = table.Indexes.Any(i =>
                !i.IsDisabled && !i.IsFiltered && !i.IsColumnstore && i.KeyColumns.Count > 0
                && boundColumns.Contains(i.KeyColumns[0], StringComparer.OrdinalIgnoreCase));

            if (hasSupportingIndex)
            {
                continue;
            }

            findings.Add(new SecurityPredicateIndexFinding(
                predicate.PolicyQualifiedName,
                predicate.TargetTableQualifiedName,
                SchemaObjectNameHelper.QualifyFunctionCall(call),
                boundColumns,
                table.SourcePath,
                table.SourceLine));
        }

        return
        [
            .. findings
                .OrderBy(f => f.TableQualifiedName, StringComparer.Ordinal)
                .ThenBy(f => f.PolicyQualifiedName, StringComparer.Ordinal),
        ];
    }

    /// <summary>
    /// Reparses a security predicate's call-site definition text (e.g.
    /// <c>([Security].[fn_TenantPredicate]([TenantId]))</c>) via the same throwaway-wrapper-
    /// statement trick <see cref="ComputedColumnMatcher"/>/<see cref="SchemaDependencyScanner"/>
    /// already use for a bare SELECT-list scalar expression (this text is a function-call
    /// expression, not a boolean predicate, unlike a CHECK constraint's own definition) - the only
    /// shape live mode's plain <c>sys.security_predicates.predicate_definition</c> string can ever
    /// produce.
    /// </summary>
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
