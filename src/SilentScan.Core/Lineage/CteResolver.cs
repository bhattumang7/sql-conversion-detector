using Microsoft.SqlServer.TransactSql.ScriptDom;
using SilentScan.Core.Catalog;
using SilentScan.Core.Diagnostics;

namespace SilentScan.Core.Lineage;

public static class CteResolver
{
    public static IReadOnlyDictionary<string, ResolvedRelation> Resolve(
        WithCtesAndXmlNamespaces? withClause, DatabaseCatalog catalog, IReadOnlyDictionary<string, ResolvedRelation> resolvedViews, string sourcePath, SkipLedger? ledger, string? procScope = null)
    {
        var ctes = new Dictionary<string, ResolvedRelation>(catalog.IdentifierComparer);
        if (withClause is null)
        {
            return ctes;
        }

        foreach (var cte in withClause.CommonTableExpressions)
        {
            var name = cte.ExpressionName.Value;
            var columns = ReferencesSelf(cte.QueryExpression, name, catalog.IdentifierComparer)
                ? ResolveRecursiveAnchor(cte, catalog, resolvedViews, ctes, sourcePath, ledger, procScope)
                : QueryExpressionResolver.Resolve(cte.QueryExpression, catalog, resolvedViews, sourcePath, ledger, ctes, procScope);

            if (cte.Columns.Count > 0)
            {
                if (columns.Count == cte.Columns.Count)
                {
                    columns = [.. columns.Zip(cte.Columns, (c, id) => c with { Name = id.Value })];
                }
                else
                {

                    ledger?.Record(
                        AnalysisPass.Lineage, sourcePath, cte.StartLine, cte.StartColumn, "CTE column list",
                        $"'{name}' declares {cte.Columns.Count} column name(s) but its query resolved {columns.Count} - column identity can't be trusted");
                    columns = [.. columns.Select((c, i) => new ResolvedColumn(
                        i < cte.Columns.Count ? cte.Columns[i].Value : c.Name,
                        new ColumnProvenance.Unknown("CTE's declared column count does not match its resolved query")))];
                }
            }

            ctes[name] = new ResolvedRelation(QualifiedName: null, columns);
        }

        return ctes;
    }

    private static List<ResolvedColumn> ResolveRecursiveAnchor(
        CommonTableExpression cte, DatabaseCatalog catalog, IReadOnlyDictionary<string, ResolvedRelation> resolvedViews,
        IReadOnlyDictionary<string, ResolvedRelation> priorCtes, string sourcePath, SkipLedger? ledger, string? procScope)
    {
        var name = cte.ExpressionName.Value;
        ledger?.Record(
            AnalysisPass.Lineage, sourcePath, cte.StartLine, cte.StartColumn, "recursive CTE",
            $"'{name}' is a recursive CTE - only the anchor member was resolved; T-SQL requires the recursive member's column types to match the anchor's exactly (Msg 240), so the anchor's types are used directly, with any base-table index claim dropped (a recursive CTE materializes through a spool, not a direct index access)");

        var branches = FlattenUnionBranches(cte.QueryExpression);
        if (branches.Count < 2 || ReferencesSelf(branches[0], name, catalog.IdentifierComparer))
        {

            return [];
        }

        var anchorCount = branches.TakeWhile(b => !ReferencesSelf(b, name, catalog.IdentifierComparer)).Count();
        if (anchorCount != 1 || !branches.Skip(1).All(b => ReferencesSelf(b, name, catalog.IdentifierComparer)))
        {

            return [];
        }

        var anchorExpression = branches[0];
        var anchorColumns = QueryExpressionResolver.Resolve(anchorExpression, catalog, resolvedViews, sourcePath, ledger, priorCtes, procScope);
        return [.. anchorColumns.Select(c => c with
        {
            Provenance = c.Provenance switch
            {
                ColumnProvenance.BaseColumn { Type: { } type } => new ColumnProvenance.Declared(type, TableQualifiedName: name),

                ColumnProvenance.BaseColumn => new ColumnProvenance.Unknown($"recursive CTE '{name}' anchor column has an unresolved declared type"),
                _ => c.Provenance,
            },
        })];
    }

    private static List<QueryExpression> FlattenUnionBranches(QueryExpression queryExpression)
    {
        var unwrapped = UnwrapParentheses(queryExpression);
        if (unwrapped is not BinaryQueryExpression binary)
        {
            return [unwrapped];
        }

        var branches = FlattenUnionBranches(binary.FirstQueryExpression);
        branches.AddRange(FlattenUnionBranches(binary.SecondQueryExpression));
        return branches;
    }

    private static QueryExpression UnwrapParentheses(QueryExpression queryExpression) =>
        queryExpression is QueryParenthesisExpression parenthesis ? UnwrapParentheses(parenthesis.QueryExpression) : queryExpression;

    internal static bool ReferencesSelf(QueryExpression queryExpression, string cteName, StringComparer? identifierComparer = null)
    {
        var collector = new SelfReferenceDetector(cteName, identifierComparer ?? StringComparer.OrdinalIgnoreCase);
        queryExpression.Accept(collector);
        return collector.Found;
    }

    private sealed class SelfReferenceDetector(string cteName, StringComparer identifierComparer) : TSqlFragmentVisitor
    {
        public bool Found { get; private set; }

        public override void Visit(NamedTableReference node)
        {
            if (node.SchemaObject.SchemaIdentifier is null && identifierComparer.Equals(node.SchemaObject.BaseIdentifier.Value, cteName))
            {
                Found = true;
            }
        }
    }
}
