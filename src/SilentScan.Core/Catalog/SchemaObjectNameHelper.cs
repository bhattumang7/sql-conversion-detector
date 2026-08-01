using Microsoft.SqlServer.TransactSql.ScriptDom;

namespace SilentScan.Core.Catalog;

public static class SchemaObjectNameHelper
{
    public const string DefaultSchema = "dbo";

    public static (string? Schema, string Name) Resolve(SchemaObjectName name)
    {
        if (name.BaseIdentifier.Value.StartsWith('#'))
        {
            // Temp tables have no schema.
            return (null, name.BaseIdentifier.Value);
        }

        var schema = name.SchemaIdentifier?.Value ?? DefaultSchema;
        return (schema, name.BaseIdentifier.Value);
    }

    public static string Qualify(SchemaObjectName name)
    {
        var (schema, tableName) = Resolve(name);
        var baseName = schema is null ? tableName : $"{schema}.{tableName}";

        // A real CREATE TABLE/ALTER TABLE target never carries a database qualifier (T-SQL
        // requires USE otherdb first), so this only ever fires for a cross-database REFERENCE
        // (docs/audit-remediation-plan.md Phase 2.5) - db.dbo.T and dbo.T must be different
        // catalog keys, not collapse into the same table, or a cross-database query silently
        // inherits an unrelated table's columns.
        return name.DatabaseIdentifier is { Value.Length: > 0 } db ? $"{db.Value}.{baseName}" : baseName;
    }
}
