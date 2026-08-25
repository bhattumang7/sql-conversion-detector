using System.Text.Json.Serialization;
using SilentScan.Core.Rules;

namespace SilentScan.Core.Predicates;

public sealed record NestedViewDepthFinding(
    string ViewQualifiedName,
    int Depth,
    IReadOnlyList<string> Chain,
    IReadOnlyList<string> BaseTables,
    [property: JsonIgnore] string SourcePath,
    [property: JsonIgnore] int Line,
    FindingConfidence Confidence = FindingConfidence.High) : IFinding
{
    public string RuleId { get; } = FindingRuleIds.NestedViewDepthRuleId;

    public SourceSpan Location => new(SourcePath, Line, 1);
}

