namespace SilentScan.Core.Catalog;

public sealed record CatalogFullTextIndexColumn(string ColumnName, string? LanguageTermRaw, bool StatisticalSemantics = false);

public sealed record CatalogFullTextIndex(
    string TableQualifiedName,
    IReadOnlyList<CatalogFullTextIndexColumn> Columns,
    string SourcePath,
    int Line,
    int Column);
