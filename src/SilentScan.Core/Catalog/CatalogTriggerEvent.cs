namespace SilentScan.Core.Catalog;

public sealed record CatalogTriggerEvent(
    string TriggerQualifiedName,
    string TableQualifiedName,
    string EventTypeDescription,
    bool IsInsteadOf,
    bool IsDisabled,
    bool IsFirst,
    bool IsLast,
    string SourcePath,
    int SourceLine);
