using SilentScan.Core.Catalog;
using SilentScan.Core.Diagnostics;

namespace SilentScan.Live;

/// <summary>
/// What <c>scan-db</c> reports today: proof the live catalog connected and read cleanly, plus
/// honest accounting of anything it could not map. Module bodies (views/procs/functions/
/// triggers) are read and run through the Lineage/Predicates/Rules pipeline separately - this
/// summary is the catalog-only foundation that stage builds on, not the tool's final live-mode
/// output.
/// </summary>
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
