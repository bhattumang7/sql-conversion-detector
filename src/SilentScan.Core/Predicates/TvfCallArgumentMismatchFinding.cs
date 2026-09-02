using System.Text.Json.Serialization;
using SilentScan.Core.Rules;

namespace SilentScan.Core.Predicates;

public sealed record TvfCallArgumentMismatchFinding(
    string? CallerScopeQualifiedName,
    string CalleeQualifiedName,
    string FormalParameterName,
    string CallerExpressionDisplay,
    string CallerTypeDisplay,
    string FormalParameterTypeDisplay,
    WriteLossKind Kind,
    [property: JsonIgnore] string SourcePath,
    [property: JsonIgnore] int Line,
    [property: JsonIgnore] int Column,
    FindingConfidence Confidence = FindingConfidence.High) : IFinding
{
    public string RuleId { get; } = FindingRuleIds.TvfCallArgumentMismatchRuleId;

    public SourceSpan Location => new(SourcePath, Line, Column);
}
