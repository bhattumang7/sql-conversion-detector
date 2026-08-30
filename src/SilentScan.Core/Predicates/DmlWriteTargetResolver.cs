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

        if (CteNameHelper.IsCteReference(named.SchemaObject, withCtes, catalog.IdentifierComparer))
        {
            return null;
        }

        var qualifiedName = catalog.ResolveSynonymName(SchemaObjectNameHelper.Qualify(named.SchemaObject));
        return catalog.Find(qualifiedName) is { Kind: CatalogTableKind.Table } ? qualifiedName : null;
    }
}
