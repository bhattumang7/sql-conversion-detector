using System.Text.Json.Serialization;

namespace SilentScan.Core.Predicates;

public sealed record TriggerOrderFinding(
    string TableQualifiedName,
    string EventTypeDescription,
    IReadOnlyList<string> UnorderedTriggerNames,
    [property: JsonIgnore] string SourcePath,
    [property: JsonIgnore] int Line,
    FindingConfidence Confidence = FindingConfidence.Medium)
{
    public SourceSpan Location => new(SourcePath, Line, 1);
}
