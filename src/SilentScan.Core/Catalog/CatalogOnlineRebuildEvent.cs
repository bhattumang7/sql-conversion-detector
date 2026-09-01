namespace SilentScan.Core.Catalog;

public sealed record CatalogAlterTableRebuildEvent(
    string TableQualifiedName,
    string SourcePath,
    int SourceLine,
    int SourceColumn);

public sealed record CatalogAlterIndexAllRebuildEvent(
    string TableQualifiedName,
    string SourcePath,
    int SourceLine,
    int SourceColumn);
