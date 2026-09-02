using System.Text.Json.Serialization;
using SilentScan.Core.Rules;

namespace SilentScan.Core.Predicates;

public sealed record VectorFunctionArgumentFinding(
    string FunctionName,
    string ArgumentDescription,
    string TypeDisplay,
    string? OtherTypeDisplay,
    VectorFunctionArgumentFindingKind Kind,
    [property: JsonIgnore] string SourcePath,
    [property: JsonIgnore] int Line,
    [property: JsonIgnore] int Column,
    FindingConfidence Confidence = FindingConfidence.High) : IFinding
{
    public string RuleId { get; } = FindingRuleIds.VectorFunctionArgumentRuleId(Kind);

    public SourceSpan Location => new(SourcePath, Line, Column);
}

public enum VectorFunctionArgumentFindingKind
{
    NonVectorOperand,
    DimensionMismatch,
}
