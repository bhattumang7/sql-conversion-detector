namespace SilentScan.Core.Catalog;

/// <summary>
/// A CHECK constraint, read live from <c>sys.check_constraints</c> - engine-authoritative by
/// construction, the same reasoning <see cref="ForeignKeyRelationship"/>'s own doc comment gives:
/// replicating the engine's own constraint-resolution semantics from parsed DDL (ALTER-added
/// constraints, multi-batch definitions) is exactly the "reinventing the database-project wheel"
/// CLAUDE.md warns against. Always empty for a file-mode scan.
///
/// <paramref name="DefinitionText"/> is <c>sys.check_constraints.definition</c> verbatim (e.g.
/// <c>([Price]&gt;(0))</c>) - deliberately NOT keyed to a single column the way
/// <c>sys.check_constraints.parent_column_id</c> is: that catalog column is 0 for any table-level
/// constraint (confirmed directly against the standing Docker oracle - a two-column CHECK
/// declared as a table constraint reports <c>parent_column_id = 0</c> even though it plainly
/// references specific columns), so a scanner that needs to know exactly which column(s) a
/// definition references must reparse this text itself, the same throwaway-wrapper-statement
/// technique <see cref="Predicates.SchemaDependencyScanner"/> already uses for the identical text.
/// Defaults to empty string for every pre-existing positional-constructor call site in this
/// codebase's own tests that predates this field.
/// </summary>
public sealed record CatalogCheckConstraint(
    string ConstraintName,
    string TableQualifiedName,
    bool IsNotTrusted,
    bool IsDisabled,
    string DefinitionText = "");
