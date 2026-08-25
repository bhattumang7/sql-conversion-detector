using System.Text.Json.Serialization;
using SilentScan.Core.Rules;

namespace SilentScan.Core.Predicates;

public sealed record NotInNullableSubqueryFinding(
    string? OuterColumnName,
    string SubqueryTableQualifiedName,
    string SubqueryColumnName,
    bool SubqueryColumnIndexed,
    [property: JsonIgnore] string SourcePath,
    [property: JsonIgnore] int Line,
    [property: JsonIgnore] int Column,
    FindingConfidence Confidence = FindingConfidence.High) : IFinding
{
    public string RuleId { get; } = FindingRuleIds.NotInNullableSubqueryRuleId;

    public SourceSpan Location => new(SourcePath, Line, Column);
}

