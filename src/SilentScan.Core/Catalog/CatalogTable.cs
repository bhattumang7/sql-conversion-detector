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
    bool IsSchemaOnlyDurability = false,
    IReadOnlyList<CatalogStatisticsInfo>? Statistics = null,
    string? FilegroupName = null,
    bool FilegroupIsReadOnly = false,
    bool HasRuleConstraint = false,
    bool CdcPartitionSwitchDisallowed = false,
    string? PartitionSchemeName = null,
    bool HasFullTextIndex = false,
    IReadOnlyList<string>? SemanticFullTextColumnNames = null)
{

    public string QualifiedName => SchemaName is null ? Name : $"{SchemaName}.{Name}";

    public IReadOnlyList<CatalogStatisticsInfo> EffectiveStatistics => Statistics ?? [];

    public CatalogColumn? FindColumn(string columnName, StringComparer? identifierComparer = null)
    {
        var comparer = identifierComparer ?? StringComparer.OrdinalIgnoreCase;
        return Columns.FirstOrDefault(c => comparer.Equals(c.Name, columnName));
    }

    public bool IsIndexedColumn(string columnName, StringComparer? identifierComparer = null) => FindIndexedColumn(columnName, identifierComparer) is not null;

    public CatalogIndex? FindIndexedColumn(string columnName, StringComparer? identifierComparer = null)
    {
        var comparer = identifierComparer ?? StringComparer.OrdinalIgnoreCase;
        return Indexes.FirstOrDefault(i => !i.IsFiltered && !i.IsColumnstore && !i.IsDisabled && i.KeyColumns.Count > 0
            && comparer.Equals(i.KeyColumns[0], columnName));
    }

    public bool IsColumnStoredInAnIndex(string columnName, StringComparer? identifierComparer = null)
    {
        var comparer = identifierComparer ?? StringComparer.OrdinalIgnoreCase;
        return Indexes.Any(i => !i.IsFiltered && !i.IsColumnstore && !i.IsDisabled && !i.IsHypothetical
            && (i.KeyColumns.Any(c => comparer.Equals(c, columnName)) || i.IncludedColumns.Any(c => comparer.Equals(c, columnName))));
    }

    public bool HasSameShapeAs(CatalogTable other) =>
        Kind == other.Kind && Columns.SequenceEqual(other.Columns);
}
