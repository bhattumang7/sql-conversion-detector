using System.Text.Json.Serialization;
using SilentScan.Core.Rules;

namespace SilentScan.Core.Predicates;

public enum OuterJoinPredicateCollapseKind
{
    LeftOuterJoin,
    RightOuterJoin,
    FullOuterJoin,
}

public sealed record OuterJoinPredicateCollapseFinding(
    OuterJoinPredicateCollapseKind Kind,
    string NullSupplyingTableQualifiedName,
    string ColumnName,
    [property: JsonIgnore] string SourcePath,
    [property: JsonIgnore] int Line,
    [property: JsonIgnore] int Column,
    FindingConfidence Confidence = FindingConfidence.High) : IFinding
{
    public string RuleId { get; } = FindingRuleIds.OuterJoinPredicateCollapseRuleId;

    public SourceSpan Location => new(SourcePath, Line, Column);
}
