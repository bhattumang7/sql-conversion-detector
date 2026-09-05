using Microsoft.SqlServer.TransactSql.ScriptDom;
using SilentScan.Core.Catalog;
using SilentScan.Core.Diagnostics;

namespace SilentScan.Core.Lineage;

public static class QueryExpressionResolver
{
    private static readonly IReadOnlyDictionary<string, ResolvedRelation> EmptyCteRelations = new Dictionary<string, ResolvedRelation>();

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
            _ => ResolveUnsupported(queryExpression, sourcePath, ledger),
        };

    private static List<ResolvedColumn> ResolveUnsupported(QueryExpression queryExpression, string sourcePath, SkipLedger? ledger)
    {
        ledger?.Record(
            AnalysisPass.Lineage, sourcePath, queryExpression.StartLine, queryExpression.StartColumn,
            "query expression", $"unsupported query expression kind '{queryExpression.GetType().Name}' - resolved to zero columns");
        return [];
    }

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

        if (first.Count != second.Count)
        {

            ledger?.Record(
                AnalysisPass.Lineage, sourcePath, binary.StartLine, binary.StartColumn, "query expression",
                $"UNION/EXCEPT/INTERSECT branches resolved different column counts ({first.Count} vs {second.Count}) - column identity can't be trusted");
            var longer = first.Count > second.Count ? first : second;
            return [.. longer.Select(c => new ResolvedColumn(c.Name, new ColumnProvenance.Unknown("UNION/EXCEPT/INTERSECT branches resolved different column counts")))];
        }

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
        var (byAlias, ordered) = FromScopeResolver.Resolve(spec.FromClause, catalog, resolvedViews, sourcePath, ledger, cteRelations ?? EmptyCteRelations, procScope);
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
                    var provenance = ScalarExpressionResolver.Resolve(scalar.Expression, byAlias, ordered, sourcePath, ledger, catalog.TypeAliases, catalog);
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
        expression is ColumnReferenceExpression { MultiPartIdentifier.Identifiers: [.., { Value: { } lastIdentifier }] }
            ? lastIdentifier
            : null;
}
