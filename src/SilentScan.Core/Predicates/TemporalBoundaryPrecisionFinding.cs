using System.Text.Json.Serialization;
using SilentScan.Core.Rules;


namespace SilentScan.Core.Predicates;

public sealed record TemporalBoundaryPrecisionFinding(
    string TableQualifiedName,
    string ColumnName,
    int ColumnScale,
    int BoundaryLiteralFractionalDigits,
    string BoundaryLiteralText,
    [property: JsonIgnore] string SourcePath,
    [property: JsonIgnore] int Line,
    [property: JsonIgnore] int Column,
    FindingConfidence Confidence = FindingConfidence.High) : IFinding
{
    public string RuleId { get; } = FindingRuleIds.TemporalBoundaryPrecisionRuleId;

    public SourceSpan Location => new(SourcePath, Line, Column);
}

