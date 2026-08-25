using System.Text.Json.Serialization;
using SilentScan.Core.Rules;


namespace SilentScan.Core.Predicates;

public enum UntrustedConstraintFindingKind
{
    ForeignKey,
    CheckConstraint,
}

public sealed record UntrustedConstraintFinding(
    UntrustedConstraintFindingKind Kind,
    string ConstraintName,
    string TableQualifiedName,
    [property: JsonIgnore] string SourcePath,
    [property: JsonIgnore] int Line,
    FindingConfidence Confidence = FindingConfidence.High) : IFinding
{
    public string RuleId { get; } = FindingRuleIds.UntrustedConstraintRuleId(Kind);

    public SourceSpan Location => new(SourcePath, Line, 1);
}

