namespace SilentScan.Core.Catalog;

public sealed record CatalogDropIndexOnlineEvent(
    string TableQualifiedName,
    string IndexName,
    string SourcePath,
    int SourceLine,
    int SourceColumn);
