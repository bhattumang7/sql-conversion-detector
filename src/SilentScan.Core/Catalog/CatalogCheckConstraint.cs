namespace SilentScan.Core.Catalog;

public sealed record CatalogCheckConstraint(
    string ConstraintName,
    string TableQualifiedName,
    bool IsNotTrusted,
    bool IsDisabled,
    string DefinitionText = "");
