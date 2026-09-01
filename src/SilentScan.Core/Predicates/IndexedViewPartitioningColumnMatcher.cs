using Microsoft.SqlServer.TransactSql.ScriptDom;
using SilentScan.Core.Catalog;
using SilentScan.Core.Parsing;

namespace SilentScan.Core.Predicates;

internal enum IndexedViewPartitioningColumnAlignment
{
    Unknown,
    Aligned,
    DerivedExpression,
    DifferentColumn,
}

internal static class IndexedViewPartitioningColumnMatcher
{
    public static IndexedViewPartitioningColumnAlignment Resolve(
        DatabaseCatalog catalog, string viewQualifiedName, string viewPartitioningColumnName, string tablePartitioningColumnName)
    {
        if (!catalog.TryGetViewDefinitionText(viewQualifiedName, out var definitionText))
        {
            return IndexedViewPartitioningColumnAlignment.Unknown;
        }

        var result = SqlScriptParser.ParseText("indexed-view-definition.sql", definitionText, initialQuotedIdentifiers: true, catalog.CompatibilityLevel);
        if (result.HasErrors
            || result.Fragment is not TSqlScript { Batches: [{ Statements: [CreateViewStatement createView] }] }
            || createView.SelectStatement.QueryExpression is not QuerySpecification { FromClause.TableReferences: [NamedTableReference] } querySpec)
        {
            return IndexedViewPartitioningColumnAlignment.Unknown;
        }

        foreach (var element in querySpec.SelectElements)
        {
            if (element is not SelectScalarExpression scalar)
            {
                continue;
            }

            var outputName = scalar.ColumnName?.Value
                ?? (scalar.Expression is ColumnReferenceExpression direct ? direct.MultiPartIdentifier.Identifiers[^1].Value : null);
            if (outputName is null || !catalog.IdentifierComparer.Equals(outputName, viewPartitioningColumnName))
            {
                continue;
            }

            if (scalar.Expression is not ColumnReferenceExpression columnRef)
            {
                return IndexedViewPartitioningColumnAlignment.DerivedExpression;
            }

            var sourceColumnName = columnRef.MultiPartIdentifier.Identifiers[^1].Value;
            return catalog.IdentifierComparer.Equals(sourceColumnName, tablePartitioningColumnName)
                ? IndexedViewPartitioningColumnAlignment.Aligned
                : IndexedViewPartitioningColumnAlignment.DifferentColumn;
        }

        return IndexedViewPartitioningColumnAlignment.Unknown;
    }
}
