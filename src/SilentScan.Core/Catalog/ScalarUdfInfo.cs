namespace SilentScan.Core.Catalog;

public sealed record ScalarUdfInfo(
    ScalarUdfKind Kind,
    bool? IsSchemaBound,
    bool? EngineIsInlineable,
    string? InlineabilityBlocker,
    bool? ClrDataAccess,
    int? InlineabilityTableReferenceCount = null);
