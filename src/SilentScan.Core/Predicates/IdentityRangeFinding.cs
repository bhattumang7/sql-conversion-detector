using System.Text.Json.Serialization;
using SilentScan.Core.Rules;

namespace SilentScan.Core.Predicates;

public enum IdentityRangeFindingKind
{
    IdentityRangeNearExhaustion,
}

public sealed record IdentityRangeFinding(
    IdentityRangeFindingKind Kind,
    string TableQualifiedName,
    string ColumnName,
    string DetailText,
    [property: JsonIgnore] string SourcePath,
    [property: JsonIgnore] int Line,
    FindingConfidence Confidence = FindingConfidence.High) : IFinding
{
    public string RuleId { get; } = FindingRuleIds.IdentityRangeRuleId(Kind);

    public SourceSpan Location => new(SourcePath, Line, 1);
}

