using System.Text.Json.Serialization;
using SilentScan.Core.Rules;

namespace SilentScan.Core.Predicates;

public enum MemoryOptimizedUnsupportedIndexOptionKind
{
    ClusteredIndex,
    IncludedColumns,
    FilteredIndex,
}

public sealed record MemoryOptimizedUnsupportedIndexOptionFinding(
    string TableQualifiedName,
    string IndexName,
    MemoryOptimizedUnsupportedIndexOptionKind Kind,
    [property: JsonIgnore] string SourcePath,
    [property: JsonIgnore] int Line,
    FindingConfidence Confidence = FindingConfidence.High) : IFinding
{
    public string RuleId { get; } = FindingRuleIds.MemoryOptimizedUnsupportedIndexOptionRuleId(Kind);

    public SourceSpan Location => new(SourcePath, Line, 1);
}
