namespace SilentScan.Core.Catalog;

public sealed record CatalogSecurityPredicate(
    string PolicyQualifiedName,
    string TargetTableQualifiedName,
    string PredicateDefinitionText,
    bool IsFilterPredicate,
    bool IsPolicyEnabled);
