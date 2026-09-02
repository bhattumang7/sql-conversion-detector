using System.Text.Json.Serialization;
using SilentScan.Core.Rules;

namespace SilentScan.Core.Predicates;

public sealed record ViewCheckOptionContradictionFinding(
    string ViewQualifiedName,
    string ColumnName,
    [property: JsonIgnore] string SourcePath,
    [property: JsonIgnore] int Line,
    [property: JsonIgnore] int Column,
    FindingConfidence Confidence = FindingConfidence.High) : IFinding
{
    public string RuleId { get; } = FindingRuleIds.ViewCheckOptionContradictionRuleId;

    public SourceSpan Location => new(SourcePath, Line, Column);
}
