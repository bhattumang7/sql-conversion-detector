using System.Text.Json.Serialization;
using SilentScan.Core.Rules;

namespace SilentScan.Core.Predicates;

public enum AlwaysEncryptedAssignmentMismatchKind
{
    LiteralSource,
    EncryptionStateMismatch,
}

public sealed record AlwaysEncryptedAssignmentMismatchFinding(
    AlwaysEncryptedAssignmentMismatchKind Kind,
    string TargetTableQualifiedName,
    string TargetColumnName,
    string TargetEncryptionTypeDisplay,
    string? SourceTableQualifiedName,
    string? SourceColumnName,
    string? SourceEncryptionTypeDisplay,
    [property: JsonIgnore] string SourcePath,
    [property: JsonIgnore] int Line,
    [property: JsonIgnore] int Column,
    FindingConfidence Confidence = FindingConfidence.High) : IFinding
{
    public string RuleId { get; } = FindingRuleIds.AlwaysEncryptedAssignmentMismatchRuleId(Kind);

    public SourceSpan Location => new(SourcePath, Line, Column);
}
