namespace SilentScan.Core.Predicates;

/// <summary>
/// docs/detection-checklist.md "Second full-archive practitioner sweep" §G: "Column carries a
/// <c>DEFAULT</c> constraint and is still nullable" - a <c>DEFAULT</c> constraint only ever applies
/// when the column is OMITTED entirely from an INSERT's own column list; any caller that supplies
/// <c>NULL</c> explicitly (an ORM's generated full-column INSERT is the common real-world case)
/// bypasses the default completely, silently, with no error - a developer who added the default
/// believing it guarantees a populated column is wrong the moment any caller passes NULL.
///
/// Oracle-confirmed directly (Docker instance, disposable scratch database, dropped immediately
/// after): a nullable <c>Status varchar(20) DEFAULT ('Active')</c> column genuinely populated
/// <c>'Active'</c> for <c>INSERT ... DEFAULT VALUES</c> (the column omitted) but genuinely stored a
/// real <c>NULL</c>, not the default, for <c>INSERT ... (Status) VALUES (NULL)</c> (the column
/// supplied explicitly as NULL) - both against the identical constraint, no error either way, and
/// no code change needed to reach either outcome.
///
/// Schema-decidable, no query text needed at all: <see cref="Catalog.CatalogColumn.IsNullable"/>
/// true on a column that also carries a DEFAULT constraint (<see
/// cref="Catalog.SchemaDependencyKind.DefaultConstraint"/> in <see
/// cref="Catalog.DatabaseCatalog.SchemaExpressions"/>) is a pure catalog fact - the finding fires
/// on the schema alone, in both file mode (<see cref="Catalog.SchemaExpressionCollector"/>, real
/// DDL text and line) and live mode (<c>sys.default_constraints</c>/<c>sys.columns.is_nullable</c>,
/// <c>SilentScan.Verify.Catalog.LiveCatalogReader</c>).
///
/// <see cref="FindingConfidence.High"/>: the "any caller supplying NULL bypasses the default"
/// mechanism is unconditional, oracle-confirmed engine behavior with zero workload dependence -
/// the same certainty tier <see cref="CheckConstraintFindingKind.NullNotHandled"/> already uses
/// for an analogous "the schema reads like it protects against NULL but genuinely doesn't"
/// mismatch. SARIF Warning, not Error: unlike <see cref="CheckConstraintFinding"/> (whose
/// NullNotHandled kind proves the CONSTRAINT'S OWN stated intent - "reject bad data" - is
/// defeated), a DEFAULT is an insert-convenience feature, not a data-integrity guarantee the
/// schema claims to enforce; whether this ever bites depends entirely on whether any real caller
/// ever sends an explicit NULL, a real but workload-dependent risk rather than a proven-wrong
/// result today.
///
/// Version-insensitive: DEFAULT-only-applies-when-omitted is ancient, stable T-SQL behavior,
/// unaffected by compatibility level.
/// </summary>
public sealed record DefaultNullableConstraintFinding(
    string TableQualifiedName,
    string ColumnName,
    string DefaultDefinitionText,
    string SourcePath,
    int Line,
    FindingConfidence Confidence = FindingConfidence.High);
