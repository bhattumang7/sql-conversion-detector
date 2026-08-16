namespace SilentScan.Core.Catalog;

/// <summary>
/// A CHECK constraint, read live from <c>sys.check_constraints</c> - engine-authoritative by
/// construction, the same reasoning <see cref="ForeignKeyRelationship"/>'s own doc comment gives:
/// replicating the engine's own constraint-resolution semantics from parsed DDL (ALTER-added
/// constraints, multi-batch definitions) is exactly the "reinventing the database-project wheel"
/// CLAUDE.md warns against. Always empty for a file-mode scan.
/// </summary>
public sealed record CatalogCheckConstraint(
    string ConstraintName,
    string TableQualifiedName,
    bool IsNotTrusted,
    bool IsDisabled);
