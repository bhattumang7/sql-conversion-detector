using System.Text.Json.Serialization;
using SilentScan.Core.Rules;


namespace SilentScan.Core.Predicates;

public sealed record TriggerOrderFinding(
    string TableQualifiedName,
    string EventTypeDescription,
    IReadOnlyList<string> UnorderedTriggerNames,
    [property: JsonIgnore] string SourcePath,
    [property: JsonIgnore] int Line,
    FindingConfidence Confidence = FindingConfidence.Medium) : IFinding
{
    public string RuleId { get; } = FindingRuleIds.TriggerOrderRuleId;

    public SourceSpan Location => new(SourcePath, Line, 1);
}
