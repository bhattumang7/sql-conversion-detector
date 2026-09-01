using System.Text.Json.Serialization;
using SilentScan.Core.Rules;

namespace SilentScan.Core.Predicates;

public enum DynamicDataMaskingFindingKind
{
    PredicateExposure,

    ComputedExpressionCollapse,
}

public sealed record DynamicDataMaskingFinding(
    string TableQualifiedName,
    string ColumnName,
    string MaskingFunctionName,
    string ContextDescription,
    [property: JsonIgnore] string SourcePath,
    [property: JsonIgnore] int Line,
    [property: JsonIgnore] int Column,
    DynamicDataMaskingFindingKind Kind,
    FindingConfidence Confidence = FindingConfidence.Medium) : IFinding
{
    public string RuleId { get; } = FindingRuleIds.DynamicDataMaskingRuleId(Kind);

    public SourceSpan Location => new(SourcePath, Line, Column);
}
