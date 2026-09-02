using System.Text.Json.Serialization;
using SilentScan.Core.Rules;

namespace SilentScan.Core.Predicates;

public sealed record RestrictedImplicitAssignmentFinding(
    string TargetVariableName,
    string TargetTypeDisplay,
    string SourceVariableName,
    string SourceTypeDisplay,
    [property: JsonIgnore] string SourcePath,
    [property: JsonIgnore] int Line,
    [property: JsonIgnore] int Column,
    FindingConfidence Confidence = FindingConfidence.High) : IFinding
{
    public string RuleId { get; } = FindingRuleIds.RestrictedImplicitAssignmentRuleId;

    public SourceSpan Location => new(SourcePath, Line, Column);
}
