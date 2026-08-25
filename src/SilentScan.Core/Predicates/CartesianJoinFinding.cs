using System.Text.Json.Serialization;
using SilentScan.Core.Rules;

namespace SilentScan.Core.Predicates;

public enum CartesianJoinKind
{
    CommaJoin,
    ExplicitCrossJoin,
}

public sealed record CartesianJoinFinding(
    CartesianJoinKind Kind,
    string FirstTableQualifiedName,
    string SecondTableQualifiedName,
    [property: JsonIgnore] string SourcePath,
    [property: JsonIgnore] int Line,
    [property: JsonIgnore] int Column,
    FindingConfidence Confidence = FindingConfidence.High) : IFinding
{
    public string RuleId { get; } = FindingRuleIds.CartesianJoinRuleId(Kind);

    public SourceSpan Location => new(SourcePath, Line, Column);
}

