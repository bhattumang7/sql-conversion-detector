namespace SilentScan.Verify.Catalog;

public enum UnanalyzableModuleReason
{
    ClrAssemblyModule,

    Encrypted,

    NonStandardModuleType,

    NumberedProcedureBody,
}

public sealed record UnanalyzableModule(string SchemaName, string ObjectName, string ObjectTypeCode, UnanalyzableModuleReason Reason)
{
    public string QualifiedName => $"{SchemaName}.{ObjectName}";
}
