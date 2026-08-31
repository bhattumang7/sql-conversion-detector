using Microsoft.SqlServer.TransactSql.ScriptDom;
using SilentScan.Core.Catalog;
using SilentScan.Core.Diagnostics;
using SilentScan.Core.Parsing;
using SilentScan.Core.Predicates;
using SilentScan.Core.Common;

namespace SilentScan.Core.Lineage;

public static class SelectIntoLineagePass
{
    public static void Apply(DatabaseCatalog catalog, LineageCatalog lineage, IEnumerable<SqlParseResult> parseResults)
    {
        foreach (var result in parseResults)
        {
            var rule = new Rule(catalog, lineage.AllRelations, result.SourcePath);
            var walker = new ModuleWalker(
                result.SourcePath, catalog, lineage.AllRelations,
                rules: [rule], callerContext: new ModuleWalkerCallerContext(catalog.Skipped, null, null), triggerScopeAnalysisPass: AnalysisPass.Lineage);
            result.Fragment.Accept(walker);
        }
    }

    private sealed class Rule(DatabaseCatalog catalog, IReadOnlyDictionary<string, ResolvedRelation> resolvedViews, string sourcePath) : IModuleRule
    {
        public void OnEnterSelectStatementScope(SelectStatement node, ModuleWalker walker)
        {
            if (node.Into is not null)
            {
                ResolveSelectIntoTarget(node, walker);
            }
        }

        private void ResolveSelectIntoTarget(SelectStatement select, ModuleWalker walker)
        {
            var targetName = select.Into!;
            var qualifiedName = SchemaObjectNameHelper.Qualify(targetName);
            var isTemp = SchemaObjectNameHelper.IsLocalTempName(qualifiedName);

            var existing = catalog.Find(qualifiedName, isTemp ? walker.CurrentProcScope : null);
            if (existing is null)
            {

                catalog.Skipped.Record(
                    AnalysisPass.Lineage, sourcePath, select.StartLine, select.StartColumn, "SELECT INTO",
                    $"'{qualifiedName}' has no Pass-1 catalog entry to merge into - the SELECT INTO statement may not have reached CatalogBuilder");
                return;
            }

            var resolved = QueryExpressionResolver.Resolve(
                select.QueryExpression, catalog, resolvedViews, sourcePath, catalog.Skipped, walker.CurrentCteRelations(), walker.CurrentProcScope);

            if (existing.Columns.Count == 0 && resolved.Count > 0)
            {

                var freshColumns = resolved
                    .Select(r => new CatalogColumn(r.Name, ColumnProvenanceAnalysis.TryGetScalarType(r.Provenance), IsNullable: true, IsIdentity: false, IsComputed: false, IsPersisted: false))
                    .ToList();
                catalog.AddOrReplace(existing with { Columns = freshColumns }, isTemp ? walker.CurrentProcScope : null);
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

            catalog.AddOrReplace(existing with { Columns = mergedColumns }, isTemp ? walker.CurrentProcScope : null);
        }
    }
}
