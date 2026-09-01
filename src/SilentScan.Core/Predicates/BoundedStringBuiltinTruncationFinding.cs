using System.Text.Json.Serialization;
using SilentScan.Core.Rules;

namespace SilentScan.Core.Predicates;

public enum BoundedStringBuiltinTruncationFindingKind
{
    ReplicateResultTruncated,
    ReplaceResultTruncated,
    SpaceResultTruncated,
}

public sealed record BoundedStringBuiltinTruncationFinding(
    BoundedStringBuiltinTruncationFindingKind Kind,
    string FunctionName,
    long ComputedLength,
    int CapBytes,
    [property: JsonIgnore] string SourcePath,
    [property: JsonIgnore] int Line,
    [property: JsonIgnore] int Column,
    FindingConfidence Confidence = FindingConfidence.High) : IFinding
{
    public string RuleId { get; } = FindingRuleIds.BoundedStringBuiltinTruncationRuleId(Kind);

    public SourceSpan Location => new(SourcePath, Line, Column);
}
