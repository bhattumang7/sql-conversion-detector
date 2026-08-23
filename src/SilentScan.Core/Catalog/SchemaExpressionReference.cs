namespace SilentScan.Core.Catalog;

public sealed record SchemaExpressionReference(
    SchemaDependencyKind Kind,
    string TableQualifiedName,
    string? ColumnName,
    string DefinitionText,
    string SourcePath,
    int Line);
