using Microsoft.SqlServer.TransactSql.ScriptDom;
using SilentScan.Core.Catalog;
using SilentScan.Core.Lineage;
using SilentScan.Core.Common;

namespace SilentScan.Core.Predicates;

public static class ModuleReachableObjectWalker
{
    public readonly record struct Touch(string ObjectQualifiedName, string? IndexName, bool IsIndexedView);

    public static bool TryFindTouch(TSqlFragment moduleBody, DatabaseCatalog catalog, LineageCatalog lineage, out Touch touch)
    {
        var collector = new TableReferenceCollector();
        moduleBody.Accept(collector);

        var visitedViews = new HashSet<string>(catalog.IdentifierComparer);
        foreach (var rawName in collector.QualifiedNames)
        {
            if (TryInspectQualifiedName(catalog.ResolveSynonymName(rawName), catalog, lineage, visitedViews, out touch))
            {
                return true;
            }
        }

        touch = default;
        return false;
    }

    private static bool TryInspectQualifiedName(string qualifiedName, DatabaseCatalog catalog, LineageCatalog lineage, HashSet<string> visitedViews, out Touch touch)
    {
        if (catalog.IsIndexedView(qualifiedName))
        {
            touch = new Touch(qualifiedName, IndexName: null, IsIndexedView: true);
            return true;
        }

        if (catalog.Find(qualifiedName) is { } table)
        {
            if (table.Indexes.FirstOrDefault(i => i.IsFiltered) is { } filteredIndex)
            {
                touch = new Touch(qualifiedName, filteredIndex.Name, IsIndexedView: false);
                return true;
            }

            touch = default;
            return false;
        }

        if (!visitedViews.Add(qualifiedName) || !lineage.AllRelations.TryGetValue(qualifiedName, out var relation))
        {
            touch = default;
            return false;
        }

        var nested = relation.Columns
            .SelectMany(column => ColumnProvenanceAnalysis.FindUnderlyingBaseColumns(column.Provenance))
            .Select(baseColumn => (baseColumn.TableQualifiedName, Index: catalog.Find(baseColumn.TableQualifiedName)?.Indexes.FirstOrDefault(i => i.IsFiltered)))
            .FirstOrDefault(x => x.Index is not null);

        if (nested.Index is not null)
        {
            touch = new Touch(nested.TableQualifiedName, nested.Index.Name, IsIndexedView: false);
            return true;
        }

        touch = default;
        return false;
    }

    private sealed class TableReferenceCollector : TSqlFragmentVisitor
    {
        public List<string> QualifiedNames { get; } = [];

        public override void Visit(NamedTableReference node) =>
            QualifiedNames.Add(SchemaObjectNameHelper.Qualify(node.SchemaObject));
    }
}
