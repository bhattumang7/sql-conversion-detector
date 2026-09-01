using System.Text.Json.Serialization;
using SilentScan.Core.Rules;

namespace SilentScan.Core.Predicates;

public enum CheckConstraintPredicateContradictionKind
{
    CheckConstraintInterval,

    NotNullConstraint,
}

public sealed record CheckConstraintPredicateContradictionFinding(
    CheckConstraintPredicateContradictionKind Kind,
    string TableQualifiedName,
    string ColumnName,
    string? ConstraintName,
    [property: JsonIgnore] string SourcePath,
    [property: JsonIgnore] int Line,
    [property: JsonIgnore] int Column,
    FindingConfidence Confidence = FindingConfidence.High) : IFinding
{
    public string RuleId { get; } = FindingRuleIds.CheckConstraintPredicateContradictionRuleId(Kind);

    public SourceSpan Location => new(SourcePath, Line, Column);
}
