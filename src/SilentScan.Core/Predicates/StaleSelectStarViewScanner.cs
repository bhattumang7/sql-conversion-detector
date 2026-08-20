using Microsoft.SqlServer.TransactSql.ScriptDom;
using SilentScan.Core.Catalog;
using SilentScan.Core.Lineage;

namespace SilentScan.Core.Predicates;

/// <summary>
/// See <see cref="StaleSelectStarViewFinding"/> for the full precision story and oracle evidence.
/// Catalog-plus-AST: the AST half finds every view whose own outermost query specification is a
/// bare/qualified <c>SELECT *</c> from exactly one real base table; the catalog half compares that
/// view's live-only compiled column list (<see cref="DatabaseCatalog.TryGetViewCompiledColumns"/>)
/// against the base table's CURRENT columns. Live-mode only by construction (both catalog inputs
/// are live-only) - always returns nothing useful in file mode, matching every other stream this
/// codebase already documents as live-only for the identical "no staleness to observe from parsed
/// text alone" reason.
/// </summary>
public static class StaleSelectStarViewScanner
{
    public static IReadOnlyList<StaleSelectStarViewFinding> Scan(IReadOnlyList<ViewDefinition> views, DatabaseCatalog catalog)
    {
        var findings = new List<StaleSelectStarViewFinding>();

        foreach (var view in views)
        {
            if (FindOutermostStarLine(view.SelectStatement.QueryExpression) is null)
            {
                continue;
            }

            var cteNames = CteNamesOf(view.SelectStatement.WithCtesAndXmlNamespaces);
            if (FindSingleBaseTable(view.SelectStatement.QueryExpression, cteNames) is not { } baseTableQualifiedName)
            {
                continue;
            }

            if (!catalog.TryGetViewCompiledColumns(view.QualifiedName, out var viewColumns))
            {
                continue;
            }

            var baseTable = catalog.Find(baseTableQualifiedName);
            if (baseTable is null)
            {
                continue;
            }

            var baseTableColumns = baseTable.Columns.Select(c => c.Name).ToList();

            if (viewColumns.SequenceEqual(baseTableColumns, StringComparer.OrdinalIgnoreCase))
            {
                continue;
            }

            findings.Add(new StaleSelectStarViewFinding(
                view.QualifiedName, baseTableQualifiedName, viewColumns, baseTableColumns, view.SourcePath, view.SourceLine));
        }

        return
        [
            .. findings
                .OrderBy(f => f.ViewQualifiedName, StringComparer.Ordinal)
                .ThenBy(f => f.BaseTableQualifiedName, StringComparer.Ordinal),
        ];
    }

    /// <summary>Mirrors <see cref="SelectStarViewScanner"/>'s own identical helper - only the view's OWN outermost query specification's <c>*</c> qualifies; a <c>*</c> nested only inside an inner derived-table subquery does not, and a top-level UNION declines rather than guessing which branch's star matters.</summary>
    private static int? FindOutermostStarLine(QueryExpression queryExpression) =>
        queryExpression switch
        {
            QueryParenthesisExpression parenthesis => FindOutermostStarLine(parenthesis.QueryExpression),
            QuerySpecification spec => spec.SelectElements.OfType<SelectStarExpression>().Select(s => (int?)s.StartLine).FirstOrDefault(),
            _ => null,
        };

    /// <summary>
    /// Only a single, real, named base table (no join, no derived table, no CTE) qualifies - a
    /// documented v1 scope limit, matching <see cref="StaleSelectStarViewFinding"/>'s own doc
    /// comment. An unqualified reference sharing one of the view's OWN CTE names is declined
    /// rather than resolved against the catalog as if it were a real table sharing that name - a
    /// CTE is never schema-qualified, so it always shadows a same-named real base table for the
    /// view's own body (the same bug class fixed across every other FROM-clause resolver in this
    /// codebase); this scanner inspects only <c>QueryExpression</c>, never the separate
    /// <c>WithCtesAndXmlNamespaces</c> the CTE itself is declared on, so the check has to be
    /// threaded in explicitly rather than falling out of the AST walk.
    /// </summary>
    private static string? FindSingleBaseTable(QueryExpression queryExpression, HashSet<string> cteNames) =>
        queryExpression switch
        {
            QueryParenthesisExpression parenthesis => FindSingleBaseTable(parenthesis.QueryExpression, cteNames),
            QuerySpecification { FromClause.TableReferences: [NamedTableReference namedTable] }
                when namedTable.SchemaObject.SchemaIdentifier is not null || !cteNames.Contains(namedTable.SchemaObject.BaseIdentifier.Value) =>
                SchemaObjectNameHelper.Qualify(namedTable.SchemaObject),
            _ => null,
        };

    private static HashSet<string> CteNamesOf(WithCtesAndXmlNamespaces? withClause) =>
        withClause is { CommonTableExpressions: { } ctes }
            ? new HashSet<string>(ctes.Select(cte => cte.ExpressionName.Value), StringComparer.OrdinalIgnoreCase)
            : [];
}
