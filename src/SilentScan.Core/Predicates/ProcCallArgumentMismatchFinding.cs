using System.Text.Json.Serialization;
using SilentScan.Core.Rules;

namespace SilentScan.Core.Predicates;

public sealed record ProcCallArgumentMismatchFinding(
    string? CallerScopeQualifiedName,
    string CalleeQualifiedName,
    string FormalParameterName,
    string CallerVariableName,
    string CallerTypeDisplay,
    string FormalParameterTypeDisplay,
    WriteLossKind Kind,
    bool IsOutputWriteback,
    [property: JsonIgnore] string SourcePath,
    [property: JsonIgnore] int Line,
    [property: JsonIgnore] int Column,
    FindingConfidence Confidence = FindingConfidence.High) : IFinding
{
    public string RuleId { get; } = FindingRuleIds.ProcCallArgumentMismatchRuleId;

    public SourceSpan Location => new(SourcePath, Line, Column);
}

