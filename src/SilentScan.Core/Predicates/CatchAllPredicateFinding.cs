using System.Text.Json.Serialization;
using SilentScan.Core.Rules;

namespace SilentScan.Core.Predicates;

public sealed record CatchAllPredicateFinding(
    string TableQualifiedName,
    string ColumnName,
    bool Indexed,
    string ParameterName,
    [property: JsonIgnore] string SourcePath,
    [property: JsonIgnore] int Line,
    [property: JsonIgnore] int Column,
    FindingConfidence Confidence = FindingConfidence.High) : IFinding
{
    public string RuleId { get; } = FindingRuleIds.CatchAllPredicateRuleId;

    public SourceSpan Location => new(SourcePath, Line, Column);
}

