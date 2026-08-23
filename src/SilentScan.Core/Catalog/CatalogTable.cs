namespace SilentScan.Core.Catalog;

public enum CatalogTableKind
{
    Table,
    TemporaryTable,
    TableVariable,

TableType,

ClrTableValuedFunction,
}

public sealed record CatalogTable(
    string? SchemaName,
    string Name,
    CatalogTableKind Kind,
    IReadOnlyList<CatalogColumn> Columns,
    IReadOnlyList<CatalogIndex> Indexes,
    string SourcePath,
    int SourceLine,
    bool IsMemoryOptimized = false,
    IReadOnlyList<CatalogStatisticsInfo>? Statistics = null,
    string? FilegroupName = null,
    bool FilegroupIsReadOnly = false,
    bool HasRuleConstraint = false,
    bool CdcPartitionSwitchDisallowed = false,
    string? PartitionSchemeName = null,
    bool HasFullTextIndex = false)
{

public string QualifiedName => SchemaName is null ? Name : $"{SchemaName}.{Name}";

public IReadOnlyList<CatalogStatisticsInfo> EffectiveStatistics => Statistics ?? [];

    public CatalogColumn? FindColumn(string columnName) =>
        Columns.FirstOrDefault(c => string.Equals(c.Name, columnName, StringComparison.OrdinalIgnoreCase));

public bool IsIndexedColumn(string columnName) => FindIndexedColumn(columnName) is not null;

public CatalogIndex? FindIndexedColumn(string columnName) =>
        Indexes.FirstOrDefault(i => !i.IsFiltered && !i.IsColumnstore && !i.IsDisabled && i.KeyColumns.Count > 0
            && string.Equals(i.KeyColumns[0], columnName, StringComparison.OrdinalIgnoreCase));

public bool HasSameShapeAs(CatalogTable other) =>
        Kind == other.Kind && Columns.SequenceEqual(other.Columns);
}
