using Microsoft.SqlServer.TransactSql.ScriptDom;
using SilentScan.Core.Catalog;
using SilentScan.Core.Diagnostics;

namespace SilentScan.Core.Lineage;

/// <summary>
/// Resolves a query's output columns, recursing through UNION/EXCEPT/INTERSECT branches and
/// parenthesized queries down to each leaf <see cref="QuerySpecification"/>.
/// </summary>
public static class QueryExpressionResolver
{
    public static List<ResolvedColumn> Resolve(
        QueryExpression queryExpression,
        DatabaseCatalog catalog,
        IReadOnlyDictionary<string, ResolvedRelation> resolvedViews,
        string sourcePath,
        SkipLedger? ledger = null,
        IReadOnlyDictionary<string, ResolvedRelation>? cteRelations = null,
        string? procScope = null) =>
        queryExpression switch
        {
            QuerySpecification spec => ResolveQuerySpecification(spec, catalog, resolvedViews, sourcePath, ledger, cteRelations, procScope),
            BinaryQueryExpression binary => ResolveBinary(binary, catalog, resolvedViews, sourcePath, ledger, cteRelations, procScope),
            QueryParenthesisExpression parenthesis => Resolve(parenthesis.QueryExpression, catalog, resolvedViews, sourcePath, ledger, cteRelations, procScope),
            _ => [],
        };

    private static List<ResolvedColumn> ResolveBinary(
        BinaryQueryExpression binary,
        DatabaseCatalog catalog,
        IReadOnlyDictionary<string, ResolvedRelation> resolvedViews,
        string sourcePath,
        SkipLedger? ledger,
        IReadOnlyDictionary<string, ResolvedRelation>? cteRelations,
        string? procScope)
    {
        var first = Resolve(binary.FirstQueryExpression, catalog, resolvedViews, sourcePath, ledger, cteRelations, procScope);
        var second = Resolve(binary.SecondQueryExpression, catalog, resolvedViews, sourcePath, ledger, cteRelations, procScope);

        // CLAUDE.md: "UNION/UNION ALL output type = highest precedence across branches
        // (record ALL branch types - the mixed-branch case is itself a finding)." The left
        // branch's column name wins, matching T-SQL's own output-naming rule.
        return [.. first.Zip(second, (f, s) => new ResolvedColumn(f.Name, new ColumnProvenance.Union([f.Provenance, s.Provenance])))];
    }

    private static List<ResolvedColumn> ResolveQuerySpecification(
        QuerySpecification spec,
        DatabaseCatalog catalog,
        IReadOnlyDictionary<string, ResolvedRelation> resolvedViews,
        string sourcePath,
        SkipLedger? ledger,
        IReadOnlyDictionary<string, ResolvedRelation>? cteRelations,
        string? procScope)
    {
        var (byAlias, ordered) = FromScopeResolver.Resolve(spec.FromClause, catalog, resolvedViews, sourcePath, ledger, cteRelations, procScope);
        var result = new List<ResolvedColumn>();

        foreach (var element in spec.SelectElements)
        {
            switch (element)
            {
                case SelectStarExpression star:
                    result.AddRange(ResolveStar(star, byAlias, ordered, sourcePath, ledger));
                    break;

                case SelectScalarExpression scalar:
                    var name = scalar.ColumnName?.Value ?? InferName(scalar.Expression);
                    var provenance = ScalarExpressionResolver.Resolve(scalar.Expression, byAlias, ordered, sourcePath, ledger);
                    result.Add(new ResolvedColumn(name ?? "?column?", provenance));
                    break;
            }
        }

        return result;
    }

    private static IEnumerable<ResolvedColumn> ResolveStar(
        SelectStarExpression star, Dictionary<string, ScopeEntry> byAlias, IReadOnlyList<ScopeEntry> ordered, string sourcePath, SkipLedger? ledger)
    {
        if (star.Qualifier is { Count: > 0 } qualifier)
        {
            var aliasName = qualifier.Identifiers[^1].Value;
            if (byAlias.TryGetValue(aliasName, out var entry))
            {
                return BumpAll(entry);
            }

            ledger?.Record(AnalysisPass.Lineage, sourcePath, star.StartLine, star.StartColumn, "SELECT *", $"unknown table alias '{aliasName}' in SELECT {aliasName}.*");
            return [new ResolvedColumn("*", new ColumnProvenance.Unknown($"unknown table alias '{aliasName}' in SELECT *"))];
        }

        return ordered.SelectMany(BumpAll);
    }

    private static IEnumerable<ResolvedColumn> BumpAll(ScopeEntry entry) =>
        entry.Relation.Columns.Select(c => c with { Provenance = ScalarExpressionResolver.BumpDepthIfViewLayer(c.Provenance, entry.IsViewLayer) });

    private static string? InferName(ScalarExpression expression) =>
        expression is ColumnReferenceExpression columnRef ? columnRef.MultiPartIdentifier.Identifiers[^1].Value : null;
}
