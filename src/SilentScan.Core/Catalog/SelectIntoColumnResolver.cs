using Microsoft.SqlServer.TransactSql.ScriptDom;
using SilentScan.Core.Diagnostics;
using SilentScan.Core.TypeInference;
using SilentScan.Core.Common;

namespace SilentScan.Core.Catalog;

internal static class SelectIntoColumnResolver
{
    public static List<CatalogColumn> Resolve(
        SelectStatement select, DatabaseCatalog catalog, string? scope, string sourcePath, SkipLedger ledger)
    {
        if (select.QueryExpression is not QuerySpecification spec)
        {
            ledger.Record(
                AnalysisPass.Catalog, sourcePath, select.StartLine, select.StartColumn,
                "SELECT INTO", "source is not a simple query specification (e.g. a UNION) - target table columns are unresolved");
            return [];
        }

        var cteNames = CteNamesOf(select);
        var fromScope = ResolveFromScope(spec.FromClause, catalog, scope, cteNames);
        var columns = new List<CatalogColumn>();

        foreach (var element in spec.SelectElements)
        {
            switch (element)
            {
                case SelectStarExpression star:
                    columns.AddRange(ResolveStar(star, fromScope));
                    break;

                case SelectScalarExpression { Expression: ColumnReferenceExpression columnRef } scalar:
                    var columnName = scalar.ColumnName?.Value ?? columnRef.MultiPartIdentifier.Identifiers[^1].Value;
                    columns.Add(new CatalogColumn(columnName, ResolveColumnType(columnRef, fromScope), IsNullable: true, IsIdentity: false, IsComputed: false, IsPersisted: false));
                    break;

                case SelectScalarExpression { ColumnName.Value: { } aliasedName }:

                    columns.Add(new CatalogColumn(aliasedName, null, IsNullable: true, IsIdentity: false, IsComputed: false, IsPersisted: false));
                    break;

                case SelectScalarExpression unnamed:
                    ledger.Record(
                        AnalysisPass.Catalog, sourcePath, unnamed.StartLine, unnamed.StartColumn,
                        "SELECT INTO", "select element has no column name and no alias - its target column is unresolved");
                    break;
            }
        }

        return columns;
    }

    private static HashSet<string> CteNamesOf(SelectStatement select) =>
        select.WithCtesAndXmlNamespaces is { CommonTableExpressions: { } ctes }
            ? new HashSet<string>(ctes.Select(cte => cte.ExpressionName.Value), StringComparer.OrdinalIgnoreCase)
            : [];

    private static (Dictionary<string, CatalogTable?> ByAlias, List<CatalogTable?> Ordered) ResolveFromScope(
        FromClause? fromClause, DatabaseCatalog catalog, string? scope, HashSet<string> cteNames)
    {
        var byAlias = new Dictionary<string, CatalogTable?>(StringComparer.OrdinalIgnoreCase);
        var ordered = new List<CatalogTable?>();

        if (fromClause is null)
        {
            return (byAlias, ordered);
        }

        foreach (var tableReference in fromClause.TableReferences)
        {
            foreach (var leaf in FlattenJoins(tableReference))
            {
                if (leaf is not NamedTableReference named)
                {

                    ordered.Add(null);
                    continue;
                }

                if (named.SchemaObject.SchemaIdentifier is null && cteNames.Contains(named.SchemaObject.BaseIdentifier.Value))
                {
                    ordered.Add(null);
                    continue;
                }

                var qualifiedName = SchemaObjectNameHelper.Qualify(named.SchemaObject);
                var table = catalog.Find(qualifiedName, scope);
                var alias = named.Alias?.Value ?? SchemaObjectNameHelper.Resolve(named.SchemaObject).Name;

                byAlias[alias] = byAlias.ContainsKey(alias) ? null : table;
                ordered.Add(table);
            }
        }

        return (byAlias, ordered);
    }

    private static IEnumerable<TableReference> FlattenJoins(TableReference tableReference)
    {
        switch (tableReference)
        {
            case JoinTableReference join:
                foreach (var t in FlattenJoins(join.FirstTableReference))
                {
                    yield return t;
                }

                foreach (var t in FlattenJoins(join.SecondTableReference))
                {
                    yield return t;
                }

                break;

            case JoinParenthesisTableReference parenthesis:
                foreach (var t in FlattenJoins(parenthesis.Join))
                {
                    yield return t;
                }

                break;

            default:
                yield return tableReference;
                break;
        }
    }

    private static SqlType? ResolveColumnType(ColumnReferenceExpression columnRef, (Dictionary<string, CatalogTable?> ByAlias, List<CatalogTable?> Ordered) fromScope)
    {
        var identifiers = columnRef.MultiPartIdentifier.Identifiers;
        var columnName = identifiers[^1].Value;

        if (identifiers.Count >= 2)
        {
            var qualifier = identifiers[^2].Value;
            return fromScope.ByAlias.TryGetValue(qualifier, out var table) ? table?.FindColumn(columnName)?.Type : null;
        }

        var matches = fromScope.Ordered.Where(t => t?.FindColumn(columnName) is not null).ToList();
        return matches.Count == 1 ? matches[0]!.FindColumn(columnName)!.Type : null;
    }

    private static IEnumerable<CatalogColumn> ResolveStar(
        SelectStarExpression star, (Dictionary<string, CatalogTable?> ByAlias, List<CatalogTable?> Ordered) fromScope)
    {
        if (star.Qualifier is { Count: > 0 } qualifier)
        {
            var aliasName = qualifier.Identifiers[^1].Value;
            return fromScope.ByAlias.TryGetValue(aliasName, out var table) && table is not null ? table.Columns : [];
        }

        return fromScope.Ordered.Where(t => t is not null).SelectMany(t => t!.Columns);
    }
}
