using System.Text.Json.Serialization;
using SilentScan.Core.Rules;

namespace SilentScan.Core.Predicates;

public enum AlwaysEncryptedKeyColumnKind
{
    Index,
    PrimaryKey,
    UniqueConstraint,
    Statistics,
}

public sealed record AlwaysEncryptedKeyColumnFinding(
    string TableQualifiedName,
    string ObjectName,
    AlwaysEncryptedKeyColumnKind Kind,
    string ColumnName,
    [property: JsonIgnore] string SourcePath,
    [property: JsonIgnore] int Line,
    FindingConfidence Confidence = FindingConfidence.High) : IFinding
{
    public string RuleId { get; } = FindingRuleIds.AlwaysEncryptedKeyColumnRuleId;

    public SourceSpan Location => new(SourcePath, Line, 1);
}
