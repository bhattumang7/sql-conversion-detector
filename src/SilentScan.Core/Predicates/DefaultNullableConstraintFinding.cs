using System.Text.Json.Serialization;
using SilentScan.Core.Rules;

namespace SilentScan.Core.Predicates;

public sealed record DefaultNullableConstraintFinding(
    string TableQualifiedName,
    string ColumnName,
    string DefaultDefinitionText,
    [property: JsonIgnore] string SourcePath,
    [property: JsonIgnore] int Line,
    FindingConfidence Confidence = FindingConfidence.High) : IFinding
{
    public string RuleId { get; } = FindingRuleIds.DefaultNullableConstraintRuleId;

    public SourceSpan Location => new(SourcePath, Line, 1);
}

