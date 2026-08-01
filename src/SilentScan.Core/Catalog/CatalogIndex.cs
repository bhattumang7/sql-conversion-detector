namespace SilentScan.Core.Catalog;

/// <summary>
/// <paramref name="IsFiltered"/>/<paramref name="IsColumnstore"/> exist so ranking can stop
/// treating these as a plain seekable index (docs/audit-remediation-plan.md Phase 2.5): a
/// filtered index only covers rows matching its predicate (a probe outside that predicate can't
/// use it at all), and a columnstore index has no B-tree to seek in the traditional sense.
/// <see cref="CatalogTable.IsIndexedColumn"/> excludes both.
/// </summary>
public sealed record CatalogIndex(
    string? Name,
    CatalogIndexKind Kind,
    bool IsUnique,
    IReadOnlyList<string> KeyColumns,
    IReadOnlyList<string> IncludedColumns,
    bool IsFiltered = false,
    bool IsColumnstore = false);
