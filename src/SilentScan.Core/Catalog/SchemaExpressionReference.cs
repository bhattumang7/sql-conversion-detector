namespace SilentScan.Core.Catalog;

/// <summary>
/// A computed column, DEFAULT, or CHECK constraint definition captured as TEXT rather than a
/// retained AST fragment - the same shape live mode inherently produces (<c>sys.computed_columns
/// .definition</c>/<c>sys.default_constraints.definition</c>/<c>sys.check_constraints.definition
/// </c> are just strings), so a single post-catalog pass (walks <see cref="DefinitionText"/> for
/// a scalar-UDF call, resolving against the already-complete catalog) works identically for both
/// modes rather than needing file mode's own retained-AST shortcut. Retaining raw ScriptDom
/// fragments here instead would also fight <c>ScanReportBuilder</c>'s AST-free live-mode
/// streaming design (see its own doc comment).
/// </summary>
public sealed record SchemaExpressionReference(
    SchemaDependencyKind Kind,
    string TableQualifiedName,
    string? ColumnName,
    string DefinitionText,
    string SourcePath,
    int Line);
