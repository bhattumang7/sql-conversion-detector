using System.Text.Json.Serialization;
using SilentScan.Core.Rules;

namespace SilentScan.Core.Predicates;

public sealed record MemoryOptimizedUtf8CollationFinding(
    string TableQualifiedName,
    string ColumnName,
    string TypeDisplay,
    string CollationName,
    [property: JsonIgnore] string SourcePath,
    [property: JsonIgnore] int Line,
    FindingConfidence Confidence = FindingConfidence.High) : IFinding
{
    public string RuleId { get; } = FindingRuleIds.MemoryOptimizedUtf8CollationRuleId;

    public SourceSpan Location => new(SourcePath, Line, 1);
}
