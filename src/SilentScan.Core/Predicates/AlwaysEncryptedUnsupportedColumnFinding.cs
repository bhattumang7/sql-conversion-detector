using System.Text.Json.Serialization;
using SilentScan.Core.Rules;

namespace SilentScan.Core.Predicates;

public enum AlwaysEncryptedUnsupportedColumnKind
{
    UnsupportedDataType,

    IdentityColumn,
}

public sealed record AlwaysEncryptedUnsupportedColumnFinding(
    string TableQualifiedName,
    string ColumnName,
    string? TypeDisplay,
    AlwaysEncryptedUnsupportedColumnKind Kind,
    [property: JsonIgnore] string SourcePath,
    [property: JsonIgnore] int Line,
    FindingConfidence Confidence = FindingConfidence.High) : IFinding
{
    public string RuleId { get; } = FindingRuleIds.AlwaysEncryptedUnsupportedColumnRuleId(Kind);

    public SourceSpan Location => new(SourcePath, Line, 1);
}
