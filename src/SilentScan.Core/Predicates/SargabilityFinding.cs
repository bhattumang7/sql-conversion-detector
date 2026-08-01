namespace SilentScan.Core.Predicates;

public sealed record SargabilityFinding(
    SargabilityFindingKind Kind,
    string ColumnName,
    string? Detail,
    string SourcePath,
    int Line,
    int Column,
    SourceSpan? DynamicSqlCallSite = null);
