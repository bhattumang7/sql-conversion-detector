using Microsoft.SqlServer.TransactSql.ScriptDom;
using SilentScan.Core.Catalog;
using SilentScan.Core.Diagnostics;
using SilentScan.Core.Parsing;
using SilentScan.Core.Common;

namespace SilentScan.Core.Lineage;

public static class SelectIntoLineagePass
{
    public static void Apply(DatabaseCatalog catalog, LineageCatalog lineage, IEnumerable<SqlParseResult> parseResults)
    {
        foreach (var result in parseResults)
        {
            var visitor = new Visitor(catalog, lineage.AllRelations, result.SourcePath);
            result.Fragment.Accept(visitor);
        }
    }

#pragma warning disable CS9107
    private sealed class Visitor(DatabaseCatalog catalog, IReadOnlyDictionary<string, ResolvedRelation> resolvedViews, string sourcePath)
        : ScopedRelationWalker(sourcePath, catalog, resolvedViews, catalog.Skipped, currentProcScope: null, callerScopeByCalleeScope: null)
#pragma warning restore CS9107
    {
        public override void ExplicitVisit(SelectStatement node)
        {
            PushCteScope(node.WithCtesAndXmlNamespaces);

            if (node.Into is not null)
            {
                ResolveSelectIntoTarget(node);
            }

            node.AcceptChildren(this);
            PopCteScope();
        }

        private void ResolveSelectIntoTarget(SelectStatement select)
        {
            var targetName = select.Into!;
            var (schema, _) = SchemaObjectNameHelper.Resolve(targetName);
            var isTemp = schema is null;
            var qualifiedName = SchemaObjectNameHelper.Qualify(targetName);

            var existing = catalog.Find(qualifiedName, isTemp ? CurrentProcScope : null);
            if (existing is null)
            {

                catalog.Skipped.Record(
                    AnalysisPass.Lineage, sourcePath, select.StartLine, select.StartColumn, "SELECT INTO",
                    $"'{qualifiedName}' has no Pass-1 catalog entry to merge into - the SELECT INTO statement may not have reached CatalogBuilder");
                return;
            }

            var resolved = QueryExpressionResolver.Resolve(
                select.QueryExpression, catalog, resolvedViews, sourcePath, catalog.Skipped, CurrentCteRelations(), CurrentProcScope);

            if (existing.Columns.Count == 0 && resolved.Count > 0)
            {

                var freshColumns = resolved
                    .Select(r => new CatalogColumn(r.Name, ColumnProvenanceAnalysis.TryGetScalarType(r.Provenance), IsNullable: true, IsIdentity: false, IsComputed: false, IsPersisted: false))
                    .ToList();
                catalog.AddOrReplace(existing with { Columns = freshColumns }, isTemp ? CurrentProcScope : null);
                return;
            }

            var resolvedByName = new Dictionary<string, ResolvedColumn>(catalog.IdentifierComparer);
            foreach (var r in resolved)
            {
                resolvedByName.TryAdd(r.Name, r);
            }

            var mergedColumns = existing.Columns
                .Select(column => column.Type is null && resolvedByName.TryGetValue(column.Name, out var r)
                    ? column with { Type = ColumnProvenanceAnalysis.TryGetScalarType(r.Provenance) }
                    : column)
                .ToList();

            catalog.AddOrReplace(existing with { Columns = mergedColumns }, isTemp ? CurrentProcScope : null);
        }
    }
}
