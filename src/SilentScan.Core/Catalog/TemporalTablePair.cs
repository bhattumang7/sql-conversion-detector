namespace SilentScan.Core.Catalog;

/// <summary>
/// One system-versioned temporal table's own current-table/history-table pairing - see
/// <see cref="DatabaseCatalog.AddTemporalTablePair"/> for how/why this is populated.
/// </summary>
public sealed record TemporalTablePair(string CurrentTableQualifiedName, string HistoryTableQualifiedName);
