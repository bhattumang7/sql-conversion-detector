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
    public bool IsIndexedColumn(string columnName) => FindIndexedColumn(columnName) is not null;

    /// <summary>The genuinely seekable index (same eligibility rule as <see cref="IsIndexedColumn"/>) whose leading key column is <paramref name="columnName"/>, or null if none - lets a finding name WHICH index a conversion defeats, not just whether one exists. When more than one qualifying index shares the same leading column (rare, but legal), the first declared wins - deterministic, matching this codebase's file-order-is-declaration-order convention elsewhere.</summary>
    public CatalogIndex? FindIndexedColumn(string columnName) =>
        Indexes.FirstOrDefault(i => !i.IsFiltered && !i.IsColumnstore && !i.IsDisabled && i.KeyColumns.Count > 0
            && string.Equals(i.KeyColumns[0], columnName, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Column-for-column shape equality (name, type, nullability, identity/computed/persisted -
    /// everything a predicate's own type resolution or a write-loss check could depend on),
    /// order-sensitive since two callers building the SAME #temp table would naturally declare
    /// its columns in the same order. Deliberately NOT the record's own auto-generated Equals -
    /// <see cref="Columns"/>/<see cref="Indexes"/> are <c>IReadOnlyList</c>, whose default
    /// equality is reference identity, so two structurally-identical-but-separately-parsed
    /// CatalogTable instances (the ordinary case for two different callers' own DECLAREs) would
    /// never compare equal without this. SourcePath/SourceLine/Indexes are deliberately excluded -
    /// two callers legitimately create the SAME logical #temp shape from different source
    /// locations, and per-caller physical indexes don't change what a predicate against the
    /// MERGED, caller-agnostic resolution can safely claim (still never "this seeks via an
    /// index" - <see cref="Predicates.TypedPredicateExtractor"/>'s own multi-caller resolution
    /// path reports Indexed=false regardless, the same way a UNION-merged column does).
    /// </summary>
    public bool HasSameShapeAs(CatalogTable other) =>
        Kind == other.Kind && Columns.SequenceEqual(other.Columns);
}
