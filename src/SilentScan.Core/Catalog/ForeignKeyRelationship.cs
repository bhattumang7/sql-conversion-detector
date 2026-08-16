namespace SilentScan.Core.Catalog;

/// <summary>
/// One column pair of a real foreign key constraint, read live from
/// <c>sys.foreign_key_columns</c>/<c>sys.foreign_keys</c> (a composite FK produces one entry per
/// column pair, all sharing <see cref="ConstraintName"/>). Engine-authoritative by construction -
/// see <see cref="DatabaseCatalog.AddForeignKey"/> for why this is never populated from parsed DDL.
/// </summary>
public sealed record ForeignKeyRelationship(
    string ConstraintName,
    string ParentTableQualifiedName,
    string ParentColumnName,
    string ReferencedTableQualifiedName,
    string ReferencedColumnName);
