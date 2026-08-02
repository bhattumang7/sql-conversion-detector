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
                if (columns.Count == cte.Columns.Count)
                {
                    columns = [.. columns.Zip(cte.Columns, (c, id) => c with { Name = id.Value })];
                }
                else
                {
                    // Same wrong-base-column risk as the view column-list case in
                    // LineageResolver: a position-based Zip on a count mismatch silently
                    // shifts every later declared name onto a different resolved column.
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

    /// <summary>
    /// A recursive CTE's own query text has no rows to compare against on the first (anchor)
    /// evaluation - resolving the recursive member as if it could see its own final output would
    /// be a guess, so only the anchor branch (the top-level UNION/UNION ALL side that does NOT
    /// reference the CTE's own name) is resolved. Unlike a plain UNION, this is NOT reported as
    /// a <see cref="ColumnProvenance.Union"/> of "anchor, Unknown": T-SQL enforces (Msg 240,
    /// "Types don't match between the anchor and the recursive part") that the recursive
    /// member's column types are IDENTICAL to the anchor's - a script that violates this simply
    /// doesn't compile, so the anchor's type IS the CTE's type by engine guarantee, not an
    /// unverified guess. The Union-with-Unknown wrapper this used to produce made every
    /// TypedPredicateExtractor operand under it non-eligible for a verdict at all (a Union
    /// branch is never a BaseColumn/Declared), so no predicate through any recursive CTE could
    /// ever be classified - this fixes that for the whole class, not just this one construct.
    ///
    /// The INDEX claim is a different story: a recursive CTE materializes through a stack spool,
    /// so the outer predicate is not reliably pushed into the anchor's own base-table access -
    /// same reasoning as an INSTEAD OF trigger's pseudo-table (<see cref="FromScopeResolver.ToPseudoTableRelation(ResolvedRelation, string)"/>).
    /// Any <see cref="ColumnProvenance.BaseColumn"/> in the anchor's own resolution is therefore
    /// downgraded to <see cref="ColumnProvenance.Declared"/> here - the type is real and usable
    /// for a verdict, the index is not.
    /// </summary>
    private static List<ResolvedColumn> ResolveRecursiveAnchor(
        CommonTableExpression cte, DatabaseCatalog catalog, IReadOnlyDictionary<string, ResolvedRelation> resolvedViews,
        IReadOnlyDictionary<string, ResolvedRelation> priorCtes, string sourcePath, SkipLedger? ledger, string? procScope)
    {
        var name = cte.ExpressionName.Value;
        ledger?.Record(
            AnalysisPass.Lineage, sourcePath, cte.StartLine, cte.StartColumn, "recursive CTE",
            $"'{name}' is a recursive CTE - only the anchor member was resolved; T-SQL requires the recursive member's column types to match the anchor's exactly (Msg 240), so the anchor's types are used directly, with any base-table index claim dropped (a recursive CTE materializes through a spool, not a direct index access)");

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
            Provenance = c.Provenance switch
            {
                ColumnProvenance.BaseColumn { Type: { } type } => new ColumnProvenance.Declared(type, TableQualifiedName: name),
                // Same two-armed pattern FromScopeResolver.ToPseudoTableRelation uses for an
                // INSTEAD OF trigger's pseudo-table: a BaseColumn with no resolved type at all
                // can't become Declared (its Type is non-nullable) - Unknown, explicit, rather
                // than silently left as a BaseColumn that would still claim a real index.
                ColumnProvenance.BaseColumn => new ColumnProvenance.Unknown($"recursive CTE '{name}' anchor column has an unresolved declared type"),
                _ => c.Provenance,
            },
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
