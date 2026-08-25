using System.Text.Json.Serialization;
using SilentScan.Core.Rules;

namespace SilentScan.Core.Predicates;

public sealed record ColumnstoreUnsupportedColumnTypeFinding(
    string TableQualifiedName,
    string ColumnName,
    string TypeDisplay,
    string IndexName,
    [property: JsonIgnore] string SourcePath,
    [property: JsonIgnore] int Line,
    FindingConfidence Confidence = FindingConfidence.High) : IFinding
{
    public string RuleId { get; } = FindingRuleIds.ColumnstoreUnsupportedColumnTypeRuleId;

    public SourceSpan Location => new(SourcePath, Line, 1);
}
