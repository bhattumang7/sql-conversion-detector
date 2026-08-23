namespace SilentScan.Core.Catalog;

public sealed record CatalogStatisticsInfo(
    string Name,
    bool NoRecompute,
    bool IsAutoCreated,
    IReadOnlyList<string>? KeyColumnsRaw = null)
{
    public IReadOnlyList<string> KeyColumns => KeyColumnsRaw ?? [];
}
