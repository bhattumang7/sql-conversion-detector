using Microsoft.SqlServer.TransactSql.ScriptDom;

namespace SilentScan.Core.Common;

public static class CteNameHelper
{
    public static HashSet<string> Names(WithCtesAndXmlNamespaces? withClause, StringComparer identifierComparer) =>
        withClause is { CommonTableExpressions: { } ctes }
            ? new HashSet<string>(ctes.Select(cte => cte.ExpressionName.Value), identifierComparer)
            : [];

    public static bool IsCteReference(SchemaObjectName schemaObject, WithCtesAndXmlNamespaces? withClause, StringComparer identifierComparer) =>
        schemaObject.SchemaIdentifier is null
        && withClause is { CommonTableExpressions: { } ctes }
        && ctes.Any(cte => identifierComparer.Equals(cte.ExpressionName.Value, schemaObject.BaseIdentifier.Value));
}
