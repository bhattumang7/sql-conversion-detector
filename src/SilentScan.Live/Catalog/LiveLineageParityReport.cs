namespace SilentScan.Live.Catalog;

public sealed record LiveLineageParityReport(
    IReadOnlyList<LiveLineageParityMismatch> Mismatches,
    IReadOnlyList<LiveLineageStaleMetadata> StaleCachedMetadata,
    IReadOnlyList<LiveLineageUncompilableObject> UncompilableObjects,
    IReadOnlyList<LiveLineageUnverifiedColumn> Unverified)
{
    public static readonly LiveLineageParityReport Empty = new([], [], [], []);
}

public sealed record LiveLineageParityMismatch(string QualifiedViewName, string ColumnName, string Facet, string InferredValue, string ActualValue);

public sealed record LiveLineageStaleMetadata(string QualifiedViewName, string ColumnName, string Facet, string CachedValue, string LiveValue);

public sealed record LiveLineageUncompilableObject(string QualifiedViewName, int ErrorNumber, string ErrorMessage);

public sealed record LiveLineageUnverifiedColumn(string QualifiedViewName, string ColumnName, string Reason, string InferredValue, string CachedValue);
