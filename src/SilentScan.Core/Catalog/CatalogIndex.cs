namespace SilentScan.Core.Catalog;

public sealed record CatalogIndex(
    string? Name,
    CatalogIndexKind Kind,
    bool IsUnique,
    IReadOnlyList<string> KeyColumns,
    IReadOnlyList<string> IncludedColumns,
    bool IsFiltered = false,
    bool IsColumnstore = false,
    bool IsDisabled = false,
    bool IsClustered = false,
    bool IsHypothetical = false,
    string? FilterDefinition = null,
    IReadOnlyList<bool>? KeyColumnIsDescendingRaw = null,
    bool OptimizeForSequentialKey = false,
    string? PartitionSchemeName = null,
    string? PartitioningColumnName = null,
    bool IgnoreDupKey = false,
    bool IsXmlIndex = false,
    bool IsSpatialIndex = false,
    bool IsJsonIndex = false,
    bool AllowRowLocks = true,
    bool AllowPageLocks = true)
{
    public IReadOnlyList<bool> KeyColumnIsDescending => KeyColumnIsDescendingRaw ?? [];
}
