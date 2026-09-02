using System.Text.Json.Serialization;
using SilentScan.Core.Rules;

namespace SilentScan.Core.Predicates;

public sealed record JsonIndexRewriteFinding(
    string TableQualifiedName,
    string ColumnName,
    string JsonPath,
    string PredicateFragmentText,
    [property: JsonIgnore] string SourcePath,
    [property: JsonIgnore] int Line,
    [property: JsonIgnore] int Column,
    FindingConfidence Confidence = FindingConfidence.High) : IFinding
{
    public string RuleId { get; } = FindingRuleIds.JsonIndexRewriteEligibleRuleId;

    public SourceSpan Location => new(SourcePath, Line, Column);
}
