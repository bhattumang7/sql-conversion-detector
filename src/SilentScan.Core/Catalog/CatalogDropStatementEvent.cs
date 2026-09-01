namespace SilentScan.Core.Catalog;

public sealed record CatalogDropSchemaEvent(
    string SchemaName,
    string SourcePath,
    int SourceLine,
    int SourceColumn);

public sealed record CatalogDropRoleEvent(
    string RoleName,
    string SourcePath,
    int SourceLine,
    int SourceColumn);
