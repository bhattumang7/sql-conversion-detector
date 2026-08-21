namespace SilentScan.Core.Catalog;

/// <summary>
/// One row of <c>sys.stats</c> for a table - read live-only (<c>LiveCatalogReader</c>), the same
/// "engine-only fact, file mode never sees it" reasoning as every other physical-design field this
/// catalog carries (<see cref="CatalogIndex.IsClustered"/>/<see cref="CatalogIndex.IsHypothetical"/>).
/// Every index implicitly owns a matching stats object, but <c>sys.stats</c> also carries
/// auto-created single-column stats with no backing index at all - this models the statistics
/// object itself, distinct from (and not assumed to correspond 1:1 with) <see cref="CatalogIndex"/>.
/// </summary>
public sealed record CatalogStatisticsInfo(
    string Name,
    bool NoRecompute,
    bool IsAutoCreated,
    IReadOnlyList<string>? KeyColumnsRaw = null)
{
    public IReadOnlyList<string> KeyColumns => KeyColumnsRaw ?? [];
}
