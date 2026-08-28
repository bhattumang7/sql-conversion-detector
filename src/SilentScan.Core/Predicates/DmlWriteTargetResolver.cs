using Microsoft.SqlServer.TransactSql.ScriptDom;
using SilentScan.Core.Catalog;
using SilentScan.Core.Common;

namespace SilentScan.Core.Predicates;

internal static class DmlWriteTargetResolver
{
    public static string? TryResolve(TableReference? target, WithCtesAndXmlNamespaces? withCtes, DatabaseCatalog catalog)
    {
        if (target is not NamedTableReference named)
        {
            return null;
        }

        if (named.SchemaObject.SchemaIdentifier is null && withCtes is { CommonTableExpressions: { } ctes }
            && ctes.Any(cte => string.Equals(cte.ExpressionName.Value, named.SchemaObject.BaseIdentifier.Value, StringComparison.OrdinalIgnoreCase)))
        {
            return null;
        }

        var qualifiedName = catalog.ResolveSynonymName(SchemaObjectNameHelper.Qualify(named.SchemaObject));
        return catalog.Find(qualifiedName) is { Kind: CatalogTableKind.Table } ? qualifiedName : null;
    }
}
