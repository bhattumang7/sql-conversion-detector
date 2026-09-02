using System.Text.Json.Serialization;
using SilentScan.Core.Rules;

namespace SilentScan.Core.Predicates;

public enum RestoreOptionConflictKind
{
    RecoveryAndNoRecovery,
    RecoveryAndStandby,
    NoRecoveryAndStandby,
}

public sealed record RestoreOptionConflictFinding(
    RestoreOptionConflictKind Kind,
    [property: JsonIgnore] string SourcePath,
    [property: JsonIgnore] int Line,
    [property: JsonIgnore] int Column,
    FindingConfidence Confidence = FindingConfidence.High) : IFinding
{
    public string RuleId { get; } = FindingRuleIds.RestoreOptionConflictRuleId;

    public SourceSpan Location => new(SourcePath, Line, Column);
}
