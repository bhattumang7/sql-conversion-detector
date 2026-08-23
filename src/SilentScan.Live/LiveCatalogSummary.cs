using SilentScan.Core.Catalog;
using SilentScan.Core.Diagnostics;

namespace SilentScan.Live;

public sealed record LiveCatalogSummary(
    string DatabaseCollation,
    int TableCount,
    int ColumnCount,
    int IndexCount,
    int TypeAliasCount,
    IReadOnlyList<SkippedConstruct> SkippedConstructs)
{
    public static LiveCatalogSummary From(DatabaseCatalog catalog) => new(
        DatabaseCollation: catalog.DefaultCollation?.Name ?? "unknown",
        TableCount: catalog.Tables.Count,
        ColumnCount: catalog.Tables.Sum(t => t.Columns.Count),
        IndexCount: catalog.Tables.Sum(t => t.Indexes.Count),
        TypeAliasCount: catalog.TypeAliases.Count,
        SkippedConstructs: catalog.Skipped.Entries);
}
