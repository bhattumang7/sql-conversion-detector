namespace SilentScan.Core.Catalog;

public enum CatalogTableKind
{
    Table,
    TemporaryTable,
    TableVariable,

    /// <summary>A <c>CREATE TYPE ... AS TABLE</c> shape - reusable as a table-valued parameter's declared type, not a queryable object in its own right (coverage-remediation-plan.md Phase 3.2).</summary>
    TableType,
}

public sealed record CatalogTable(
    string? SchemaName,
    string Name,
    CatalogTableKind Kind,
    IReadOnlyList<CatalogColumn> Columns,
    IReadOnlyList<CatalogIndex> Indexes,
    string SourcePath,
    int SourceLine)
{
    /// <summary>schema.name, or just name for temp tables/table variables which have no schema.</summary>
    public string QualifiedName => SchemaName is null ? Name : $"{SchemaName}.{Name}";

    public CatalogColumn? FindColumn(string columnName) =>
        Columns.FirstOrDefault(c => string.Equals(c.Name, columnName, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// True only if a genuinely seekable index has this column as its LEADING key column - a
    /// filtered index (only covers rows matching its own predicate) or a columnstore index (no
    /// B-tree to seek) does not count, even though the column is technically "in an index"
    /// (docs/audit-remediation-plan.md Phase 2.5). A column that is only a non-leading key
    /// column (e.g. the second column of a composite index) cannot drive an index seek on its
    /// own regardless of an implicit conversion, so it does not count either - matching
    /// SilentScan.Verify.Oracle.IndexDeploymentChecker's identical key_ordinal = 1 requirement,
    /// so the static ranking and the oracle's confirmation precondition agree on what "indexed"
    /// means (an earlier version counted ANY key-column position, which ranked composite-index
    /// second-key predicates as indexed even though the oracle correctly refuses to confirm
    /// them at all).
    /// </summary>
    public bool IsIndexedColumn(string columnName) =>
        Indexes.Any(i => !i.IsFiltered && !i.IsColumnstore && !i.IsDisabled && i.KeyColumns.Count > 0
            && string.Equals(i.KeyColumns[0], columnName, StringComparison.OrdinalIgnoreCase));
}
