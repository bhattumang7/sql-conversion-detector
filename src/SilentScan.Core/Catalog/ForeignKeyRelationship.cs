namespace SilentScan.Core.Catalog;

/// <summary>sys.foreign_keys.delete_referential_action/update_referential_action's own documented integer codes.</summary>
public enum ReferentialAction
{
    NoAction = 0,
    Cascade = 1,
    SetNull = 2,
    SetDefault = 3,
}

/// <summary>
/// One column pair of a real foreign key constraint, read live from
/// <c>sys.foreign_key_columns</c>/<c>sys.foreign_keys</c> (a composite FK produces one entry per
/// column pair, all sharing <see cref="ConstraintName"/>). Engine-authoritative by construction -
/// see <see cref="DatabaseCatalog.AddForeignKey"/> for why this is never populated from parsed DDL.
///
/// <see cref="IsNotTrusted"/>/<see cref="IsDisabled"/>/<see cref="DeleteAction"/>/
/// <see cref="UpdateAction"/> are constraint-level facts (from <c>sys.foreign_keys</c> itself, not
/// <c>sys.foreign_key_columns</c>) - repeated identically across every column-pair row of the same
/// composite constraint. Harmless duplication, not a correctness risk: every existing consumer of
/// this type already keys off one row per column pair, and a second, parallel constraint-level
/// type would only add an extra lookup for no real benefit.
/// </summary>
public sealed record ForeignKeyRelationship(
    string ConstraintName,
    string ParentTableQualifiedName,
    string ParentColumnName,
    string ReferencedTableQualifiedName,
    string ReferencedColumnName,
    bool IsNotTrusted = false,
    bool IsDisabled = false,
    ReferentialAction DeleteAction = ReferentialAction.NoAction,
    ReferentialAction UpdateAction = ReferentialAction.NoAction);
