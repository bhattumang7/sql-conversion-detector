using SilentScan.Core.TypeInference;

namespace SilentScan.Core.Catalog;

public sealed record CatalogSelectiveXmlIndexPromotedPath(
    string TableQualifiedName,
    string IndexName,
    string PathName,
    SqlType? Type);

public sealed record CatalogSecondarySelectiveXmlIndexReference(
    string TableQualifiedName,
    string SecondaryIndexName,
    string PrimaryIndexName,
    string PathName,
    string SourcePath,
    int Line);
