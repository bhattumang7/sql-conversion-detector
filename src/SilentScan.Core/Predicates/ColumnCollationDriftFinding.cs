using System.Text.Json.Serialization;
using SilentScan.Core.Rules;

namespace SilentScan.Core.Predicates;

public sealed record ColumnCollationDriftFinding(
    string TableQualifiedName,
    string ColumnName,
    string ColumnCollationName,
    string BaselineCollationName,
    bool IsTempObject,
    [property: JsonIgnore] string SourcePath,
    [property: JsonIgnore] int Line,
    FindingConfidence Confidence = FindingConfidence.Medium) : IFinding
{
    public string RuleId { get; } = FindingRuleIds.ColumnCollationDriftRuleId;

    public SourceSpan Location => new(SourcePath, Line, 1);
}

