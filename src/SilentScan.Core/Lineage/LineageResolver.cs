using Microsoft.SqlServer.TransactSql.ScriptDom;
using SilentScan.Core.Catalog;
using SilentScan.Core.Diagnostics;
using SilentScan.Core.Parsing;
using SilentScan.Core.Rules;

namespace SilentScan.Core.Lineage;

/// <summary>
/// Pass 2: resolves every view/inline-TVF output column to its <see cref="ColumnProvenance"/>
/// (CLAUDE.md Pass 2). Views are resolved in dependency order so a column's provenance
/// chains all the way down to the physical base table, however many view layers sit between.
/// </summary>
public static class LineageResolver
{
    public static LineageCatalog Resolve(DatabaseCatalog catalog, IEnumerable<SqlParseResult> parseResults)
    {
        var ledger = new SkipLedger();
        var (views, tvfs) = ViewDefinitionExtractor.Extract(parseResults, catalog.DefaultCollation);
        var (order, cyclicViews) = ViewDependencyGraph.TopologicalSort(views);

        var resolved = new Dictionary<string, ResolvedRelation>(StringComparer.OrdinalIgnoreCase);

        foreach (var tvf in tvfs)
        {
            resolved[tvf.QualifiedName] = new ResolvedRelation(
                tvf.QualifiedName,
                [.. tvf.Columns.Select(c => new ResolvedColumn(
                    c.Name,
                    c.Type is { } type
                        ? new ColumnProvenance.Declared(type)
                        : new ColumnProvenance.Unknown("multi-statement TVF column type could not be resolved")))]);
        }

        foreach (var view in order)
        {
            if (cyclicViews.Contains(view.QualifiedName))
            {
                ledger.Record(AnalysisPass.Lineage, view.SourcePath, view.SourceLine, 0, "view dependency", $"'{view.QualifiedName}' participates in a cyclic view dependency");
                resolved[view.QualifiedName] = CyclicRelation(view);
                continue;
            }

            resolved[view.QualifiedName] = ResolveView(view, catalog, resolved, ledger);
        }

        return new LineageCatalog(resolved, cyclicViews, ledger);
    }

    private static ResolvedRelation CyclicRelation(ViewDefinition view)
    {
        var reason = $"{view.QualifiedName} participates in a cyclic view dependency";
        var columnNames = view.ExplicitColumnNames ?? TryInferOutputNames(view.SelectStatement);
        return new ResolvedRelation(view.QualifiedName, [.. columnNames.Select(n => new ResolvedColumn(n, new ColumnProvenance.Unknown(reason)))]);
    }

    /// <summary>Best-effort column names for a cyclic view so its shape is still reportable, even though every column's type is Unknown.</summary>
    private static IReadOnlyList<string> TryInferOutputNames(SelectStatement selectStatement)
    {
        if (selectStatement.QueryExpression is not QuerySpecification { SelectElements: var elements })
        {
            return [];
        }

        return [.. elements.OfType<SelectScalarExpression>()
            .Select(e => e.ColumnName?.Value ?? (e.Expression as ColumnReferenceExpression)?.MultiPartIdentifier.Identifiers[^1].Value)
            .Where(n => n is not null)
            .Select(n => n!)];
    }

    private static ResolvedRelation ResolveView(ViewDefinition view, DatabaseCatalog catalog, IReadOnlyDictionary<string, ResolvedRelation> resolvedViews, SkipLedger ledger)
    {
        // A view's own WITH clause (docs/audit-remediation-plan.md Phase 2.4) is resolved once
        // and stays visible for the whole view body - CTEs are visible throughout their
        // containing statement, not scoped per nested subquery.
        var cteRelations = CteResolver.Resolve(view.SelectStatement.WithCtesAndXmlNamespaces, catalog, resolvedViews, view.SourcePath, ledger);
        var columns = QueryExpressionResolver.Resolve(view.SelectStatement.QueryExpression, catalog, resolvedViews, view.SourcePath, ledger, cteRelations);

        if (view.ExplicitColumnNames is { Count: > 0 } explicitNames)
        {
            columns = [.. columns.Zip(explicitNames, (col, name) => col with { Name = name })];
        }

        return new ResolvedRelation(view.QualifiedName, columns);
    }
}
