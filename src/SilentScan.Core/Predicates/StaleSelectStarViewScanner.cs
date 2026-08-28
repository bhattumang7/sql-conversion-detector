using Microsoft.SqlServer.TransactSql.ScriptDom;
using SilentScan.Core.Catalog;
using SilentScan.Core.Lineage;
using SilentScan.Core.Common;

namespace SilentScan.Core.Predicates;

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

            var cteNames = CteNamesOf(view.SelectStatement.WithCtesAndXmlNamespaces, catalog.IdentifierComparer);
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

            if (viewColumns.SequenceEqual(baseTableColumns, catalog.IdentifierComparer))
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

    private static int? FindOutermostStarLine(QueryExpression queryExpression) =>
        queryExpression switch
        {
            QueryParenthesisExpression parenthesis => FindOutermostStarLine(parenthesis.QueryExpression),
            QuerySpecification spec => spec.SelectElements.OfType<SelectStarExpression>().Select(s => (int?)s.StartLine).FirstOrDefault(),
            _ => null,
        };

    private static string? FindSingleBaseTable(QueryExpression queryExpression, HashSet<string> cteNames) =>
        queryExpression switch
        {
            QueryParenthesisExpression parenthesis => FindSingleBaseTable(parenthesis.QueryExpression, cteNames),
            QuerySpecification { FromClause.TableReferences: [NamedTableReference namedTable] }
                when namedTable.SchemaObject.SchemaIdentifier is not null || !cteNames.Contains(namedTable.SchemaObject.BaseIdentifier.Value) =>
                SchemaObjectNameHelper.Qualify(namedTable.SchemaObject),
            _ => null,
        };

    private static HashSet<string> CteNamesOf(WithCtesAndXmlNamespaces? withClause, StringComparer identifierComparer) =>
        withClause is { CommonTableExpressions: { } ctes }
            ? new HashSet<string>(ctes.Select(cte => cte.ExpressionName.Value), identifierComparer)
            : [];
}
