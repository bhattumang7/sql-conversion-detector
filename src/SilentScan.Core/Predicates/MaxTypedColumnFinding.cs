using System.Text.Json.Serialization;
using SilentScan.Core.Rules;

namespace SilentScan.Core.Predicates;

public enum NonIndexableColumnFindingKind
{
    MaxLength,

    LegacyLargeObject,
}

public sealed record MaxTypedColumnFinding(
    string TableQualifiedName,
    string ColumnName,
    string TypeDisplay,
    [property: JsonIgnore] string SourcePath,
    [property: JsonIgnore] int Line,
    NonIndexableColumnFindingKind Kind = NonIndexableColumnFindingKind.MaxLength,
    FindingConfidence Confidence = FindingConfidence.High) : IFinding
{
    public string RuleId { get; } = FindingRuleIds.MaxTypedColumnRuleId(Kind);

    public SourceSpan Location => new(SourcePath, Line, 1);
}

