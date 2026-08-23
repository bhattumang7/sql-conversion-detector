using Microsoft.SqlServer.TransactSql.ScriptDom;

namespace SilentScan.Core.Common;

public static class SchemaObjectNameHelper
{
    public const string DefaultSchema = "dbo";

    public static (string? Schema, string Name) Resolve(SchemaObjectName name)
    {
        if (name.BaseIdentifier.Value.StartsWith('#'))
        {
            return (null, name.BaseIdentifier.Value);
        }

        var schema = name.SchemaIdentifier?.Value ?? DefaultSchema;
        return (schema, name.BaseIdentifier.Value);
    }

    public static string Qualify(SchemaObjectName name)
    {
        var (schema, tableName) = Resolve(name);
        var baseName = schema is null ? tableName : $"{schema}.{tableName}";

        return name.DatabaseIdentifier is { Value.Length: > 0 } db ? $"{db.Value}.{baseName}" : baseName;
    }

public static string QualifyFunctionCall(FunctionCall functionCall)
    {
        var schema = functionCall.CallTarget is MultiPartIdentifierCallTarget { MultiPartIdentifier.Identifiers: [.., { } last] }
            ? last.Value
            : DefaultSchema;

        return $"{schema}.{functionCall.FunctionName.Value}";
    }
}
