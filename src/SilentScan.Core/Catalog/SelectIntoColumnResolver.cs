using Microsoft.SqlServer.TransactSql.ScriptDom;
using SilentScan.Core.Diagnostics;

namespace SilentScan.Core.Catalog;

/// <summary>
/// Infers a <c>SELECT ... INTO #target FROM ...</c> temp table's columns
/// (docs/audit-remediation-plan.md Phase 2.5, CLAUDE.md Pass 1: "temp tables (#t via SELECT
/// INTO and CREATE TABLE #t)"). Deliberately minimal: this is Pass 1, so it resolves only
/// against tables already known to the catalog (never views - those are a Pass 2/Lineage
/// concept catalog-building can't depend on without inverting the pass order), and only a bare
/// column reference or SELECT * - anything else (an expression, a function call, a literal with
/// no explicit alias) resolves to an untyped column rather than guessing its result type.
/// </summary>
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
                    // An explicitly-aliased non-column expression (arithmetic, CASE, a function
                    // call, a literal, ...) - the name is known, the type is never guessed.
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

    /// <summary>
    /// The statement's own declared CTE names, name-only (no Lineage-level resolution - CLAUDE.md's
    /// pass-ordering rule forbids catalog-building from depending on Lineage/view resolution, so
    /// this can only ever ask "is this name syntactically a CTE here," never "what does the CTE
    /// actually select"). Mirrors the same decline-set shape <c>DirectBaseTableResolver</c> used
    /// before Phase 1.5 migrated its callers onto real Lineage-level CTE resolution - that upgrade
    /// isn't available to this pass, so the decline-only shape is the correct, permanent answer
    /// here, not a stepping stone.
    /// </summary>
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
                    // A derived table, CTE, or table-valued source - not a base table this pass
                    // can resolve without Lineage-level query resolution; left unresolved.
                    ordered.Add(null);
                    continue;
                }

                // A CTE reference parses as an ordinary NamedTableReference, identical in shape to
                // a real base table - and a CTE is never schema-qualified, so it always shadows a
                // same-named real base table for this statement's own lifetime. Resolving against
                // the catalog anyway (the previous behavior) would silently attribute a SELECT
                // INTO target column's type to an unrelated real table sharing the CTE's name -
                // the exact bug class fixed across seven Predicates-layer scanners in Phase 1.5,
                // present here too until this fix. Declined (left unresolved), never guessed at.
                if (named.SchemaObject.SchemaIdentifier is null && cteNames.Contains(named.SchemaObject.BaseIdentifier.Value))
                {
                    ordered.Add(null);
                    continue;
                }

                var qualifiedName = SchemaObjectNameHelper.Qualify(named.SchemaObject);
                var table = catalog.Find(qualifiedName, scope);
                var alias = named.Alias?.Value ?? SchemaObjectNameHelper.Resolve(named.SchemaObject).Name;
                byAlias[alias] = table;
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
