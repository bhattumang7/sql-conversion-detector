using System.Text.Json.Serialization;
using SilentScan.Core.Rules;

namespace SilentScan.Core.Predicates;

public sealed record DanglingObjectReferenceFinding(
    string ModuleQualifiedName,
    string ModuleTypeDescription,
    string ReferencedEntityName,
    string? ReferencedSchemaName,
    [property: JsonIgnore] string SourcePath,
    [property: JsonIgnore] int Line,
    [property: JsonIgnore] int Column,
    FindingConfidence Confidence = FindingConfidence.High) : IFinding
{
    public string RuleId { get; } = FindingRuleIds.DanglingObjectReferenceRuleId;

    public SourceSpan Location => new(SourcePath, Line, Column);
}
