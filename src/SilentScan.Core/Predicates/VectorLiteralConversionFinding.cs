using System.Text.Json.Serialization;
using SilentScan.Core.Rules;

namespace SilentScan.Core.Predicates;

public sealed record VectorLiteralConversionFinding(
    string LiteralText,
    string TargetTypeDisplay,
    string? ElementKind,
    int? ActualElementCount,
    int? DeclaredDimensions,
    VectorLiteralConversionFindingKind Kind,
    [property: JsonIgnore] string SourcePath,
    [property: JsonIgnore] int Line,
    [property: JsonIgnore] int Column,
    FindingConfidence Confidence = FindingConfidence.High) : IFinding
{
    public string RuleId { get; } = FindingRuleIds.VectorLiteralConversionRuleId(Kind);

    public SourceSpan Location => new(SourcePath, Line, Column);
}

public enum VectorLiteralConversionFindingKind
{
    NonNumericJsonElement,
    ElementCountMismatch,
}
