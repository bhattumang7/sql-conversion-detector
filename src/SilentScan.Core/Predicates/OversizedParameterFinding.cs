using System.Text.Json.Serialization;
using SilentScan.Core.Rules;

namespace SilentScan.Core.Predicates;

public sealed record OversizedParameterFinding(
    string TableQualifiedName,
    string ColumnName,
    int ColumnLength,
    int OtherOperandLength,
    [property: JsonIgnore] string SourcePath,
    [property: JsonIgnore] int Line,
    [property: JsonIgnore] int Column,
    FindingConfidence Confidence = FindingConfidence.Low) : IFinding
{
    public string RuleId { get; } = FindingRuleIds.OversizedParameterRuleId;

    public SourceSpan Location => new(SourcePath, Line, Column);
}

