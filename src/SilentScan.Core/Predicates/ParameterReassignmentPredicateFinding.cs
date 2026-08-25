using System.Text.Json.Serialization;
using SilentScan.Core.Rules;

namespace SilentScan.Core.Predicates;

public sealed record ParameterReassignmentPredicateFinding(
    string TableQualifiedName,
    string ColumnName,
    bool Indexed,
    string ParameterName,
    string Operator,
    int ReassignmentLine,
    int ReassignmentColumn,
    [property: JsonIgnore] string SourcePath,
    [property: JsonIgnore] int Line,
    [property: JsonIgnore] int Column,
    FindingConfidence Confidence = FindingConfidence.Low) : IFinding
{
    public string RuleId { get; } = FindingRuleIds.ParameterReassignmentPredicateRuleId;

    public SourceSpan Location => new(SourcePath, Line, Column);
}

