using System.Text.Json.Serialization;

namespace SilentScan.Core.Predicates;

public enum MemoryOptimizedUnsupportedIndexOptionKind
{
    ClusteredIndex,
    IncludedColumns,
}

public sealed record MemoryOptimizedUnsupportedIndexOptionFinding(
    string TableQualifiedName,
    string IndexName,
    MemoryOptimizedUnsupportedIndexOptionKind Kind,
    [property: JsonIgnore] string SourcePath,
    [property: JsonIgnore] int Line,
    FindingConfidence Confidence = FindingConfidence.High)
{
    public SourceSpan Location => new(SourcePath, Line, 1);
}
