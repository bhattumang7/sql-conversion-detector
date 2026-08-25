using System.Text.Json.Serialization;
using SilentScan.Core.Rules;

namespace SilentScan.Core.Predicates;

public sealed record OperandComparabilityFinding(
    string TableQualifiedName,
    string ColumnName,
    string TypeDisplay,
    OperandComparabilityFindingKind Kind,
    OperandComparabilityContext Context,
    string? OperatorText,
    [property: JsonIgnore] string SourcePath,
    [property: JsonIgnore] int Line,
    [property: JsonIgnore] int Column,
    FindingConfidence Confidence = FindingConfidence.High) : IFinding
{
    public string RuleId { get; } = FindingRuleIds.OperandComparabilityRuleId(Kind);

    public SourceSpan Location => new(SourcePath, Line, Column);
}

public enum OperandComparabilityFindingKind
{
    Xml,
    LegacyLargeObject,
}

public enum OperandComparabilityContext
{
    Comparison,
    In,
    Between,
    NullIf,
    OrderBy,
    GroupBy,
    Distinct,
}
