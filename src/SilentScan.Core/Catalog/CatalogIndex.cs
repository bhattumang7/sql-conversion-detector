namespace SilentScan.Core.Catalog;

/// <summary>
/// <paramref name="IsFiltered"/>/<paramref name="IsColumnstore"/> exist so ranking can stop
/// treating these as a plain seekable index (docs/audit-remediation-plan.md Phase 2.5): a
/// filtered index only covers rows matching its predicate (a probe outside that predicate can't
/// use it at all), and a columnstore index has no B-tree to seek in the traditional sense.
/// <paramref name="IsDisabled"/> covers <c>ALTER INDEX ... DISABLE</c> - a disabled index still
/// exists in the catalog (so a later <c>REBUILD</c> can re-enable it) but is genuinely unusable
/// by the engine in the meantime; reporting Indexed=true for it would be the wrong direction for
/// CLAUDE.md's precision discipline. <see cref="CatalogTable.IsIndexedColumn"/> excludes all
/// three.
/// </summary>
public sealed record CatalogIndex(
    string? Name,
    CatalogIndexKind Kind,
    bool IsUnique,
    IReadOnlyList<string> KeyColumns,
    IReadOnlyList<string> IncludedColumns,
    bool IsFiltered = false,
    bool IsColumnstore = false,
    bool IsDisabled = false);
