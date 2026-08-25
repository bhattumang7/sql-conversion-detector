using System.Text.Json.Serialization;
using SilentScan.Core.Rules;

namespace SilentScan.Core.Predicates;

public sealed record LocalVariablePredicateFinding(
    string TableQualifiedName,
    string ColumnName,
    bool? Indexed,
    int Depth,
    string VariableName,
    string Operator,
    [property: JsonIgnore] string SourcePath,
    [property: JsonIgnore] int Line,
    [property: JsonIgnore] int Column,
    FindingConfidence Confidence = FindingConfidence.Low) : IFinding
{
    public string RuleId { get; } = FindingRuleIds.LocalVariablePredicateRuleId;

    public SourceSpan Location => new(SourcePath, Line, Column);
}

