namespace SilentScan.Verify.Catalog;

public sealed record LiveModule(
    string SchemaName,
    string ObjectName,
    string ObjectTypeCode,
    string Definition,
    bool UsesQuotedIdentifier,
    bool UsesAnsiNulls,
    bool IsSchemaBound,
    bool IsRecompiled,
    bool UsesDatabaseCollation)
{
    public string QualifiedName => $"{SchemaName}.{ObjectName}";
}
