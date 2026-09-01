using SilentScan.Core.TypeInference;

namespace SilentScan.Core.Catalog;

public sealed record CatalogAlterColumnEvent(
    string TableQualifiedName,
    string ColumnName,
    SqlType? PreviousType,
    SqlType? NewType,
    string SourcePath,
    int SourceLine,
    bool IsOnline = false,
    int SourceColumn = 0);
