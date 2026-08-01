using Microsoft.SqlServer.TransactSql.ScriptDom;
using SilentScan.Core.Catalog;
using SilentScan.Core.Diagnostics;

namespace SilentScan.Core.Lineage;

/// <summary>
/// Resolves a statement's <c>WITH ... AS (...)</c> common table expressions to inline relations
/// (docs/audit-remediation-plan.md Phase 2.4). A CTE is not a persisted view/TVF, so it adds no
/// view-layer depth - the same treatment <see cref="FromScopeResolver"/> already gives a derived
/// table subquery. CTE names shadow catalog tables/views of the same name for the lifetime of
/// the statement they're declared in, matching real SQL Server name resolution: a corpus table
/// named the same as a CTE is not the same object, and resolving through the catalog anyway
/// (the pre-fix behavior) silently produces wrong provenance, not just an Unknown.
/// </summary>
public static class CteResolver
{
    /// <summary>
    /// Resolves every CTE in <paramref name="withClause"/> in declaration order, so a later CTE
    /// can reference an earlier one by name (standard, non-recursive chaining) via the growing
    /// result dictionary. Returns an empty dictionary for a statement with no WITH clause.
    /// </summary>
    public static IReadOnlyDictionary<string, ResolvedRelation> Resolve(
        WithCtesAndXmlNamespaces? withClause, DatabaseCatalog catalog, IReadOnlyDictionary<string, ResolvedRelation> resolvedViews, string sourcePath, SkipLedger? ledger, string? procScope = null)
    {
        var ctes = new Dictionary<string, ResolvedRelation>(StringComparer.OrdinalIgnoreCase);
        if (withClause is null)
        {
            return ctes;
        }

        foreach (var cte in withClause.CommonTableExpressions)
        {
            var name = cte.ExpressionName.Value;
            var columns = ReferencesSelf(cte.QueryExpression, name)
                ? ResolveRecursiveAnchor(cte, catalog, resolvedViews, ctes, sourcePath, ledger, procScope)
                : QueryExpressionResolver.Resolve(cte.QueryExpression, catalog, resolvedViews, sourcePath, ledger, ctes, procScope);

            if (cte.Columns.Count > 0)
            {
                columns = [.. columns.Zip(cte.Columns, (c, id) => c with { Name = id.Value })];
            }

            ctes[name] = new ResolvedRelation(QualifiedName: null, columns);
        }

        return ctes;
    }

    /// <summary>
    /// A recursive CTE's own query text has no rows to compare against on the first (anchor)
    /// evaluation - resolving the recursive member as if it could see its own final output would
    /// be a guess, so only the anchor branch (the top-level UNION/UNION ALL side that does NOT
    /// reference the CTE's own name) is resolved. The recursive branch's contribution is recorded
    /// as <see cref="ColumnProvenance.Union"/> with an <see cref="ColumnProvenance.Unknown"/>
    /// sibling per column - CLAUDE.md: "record ALL branch types," never silently drop the branch
    /// that couldn't be resolved.
    /// </summary>
    private static List<ResolvedColumn> ResolveRecursiveAnchor(
        CommonTableExpression cte, DatabaseCatalog catalog, IReadOnlyDictionary<string, ResolvedRelation> resolvedViews,
        IReadOnlyDictionary<string, ResolvedRelation> priorCtes, string sourcePath, SkipLedger? ledger, string? procScope)
    {
        var name = cte.ExpressionName.Value;
        ledger?.Record(
            AnalysisPass.Lineage, sourcePath, cte.StartLine, cte.StartColumn, "recursive CTE",
            $"'{name}' is a recursive CTE - only the anchor member was resolved; the recursive member's own contribution is Unknown");

        if (cte.QueryExpression is not BinaryQueryExpression binary)
        {
            // Self-referencing but not a top-level UNION/UNION ALL (malformed, or a shape this
            // pass doesn't recognize as a valid recursive CTE) - nothing safe to anchor on.
            return [];
        }

        var anchorIsFirst = !ReferencesSelf(binary.FirstQueryExpression, name);
        var anchorExpression = anchorIsFirst ? binary.FirstQueryExpression : binary.SecondQueryExpression;

        var anchorColumns = QueryExpressionResolver.Resolve(anchorExpression, catalog, resolvedViews, sourcePath, ledger, priorCtes, procScope);
        return [.. anchorColumns.Select(c => c with
        {
            Provenance = new ColumnProvenance.Union([c.Provenance, new ColumnProvenance.Unknown($"recursive member of CTE '{name}' not resolved - never guess")]),
        })];
    }

    private static bool ReferencesSelf(QueryExpression queryExpression, string cteName)
    {
        var collector = new SelfReferenceDetector(cteName);
        queryExpression.Accept(collector);
        return collector.Found;
    }

    private sealed class SelfReferenceDetector(string cteName) : TSqlFragmentVisitor
    {
        public bool Found { get; private set; }

        public override void Visit(NamedTableReference node)
        {
            if (node.SchemaObject.SchemaIdentifier is null && string.Equals(node.SchemaObject.BaseIdentifier.Value, cteName, StringComparison.OrdinalIgnoreCase))
            {
                Found = true;
            }
        }
    }
}
