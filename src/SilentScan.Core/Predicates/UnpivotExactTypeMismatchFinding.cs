using System.Text.Json.Serialization;
using SilentScan.Core.Rules;

namespace SilentScan.Core.Predicates;

public sealed record UnpivotExactTypeMismatchFinding(
    string TableQualifiedName,
    string ReferenceColumnName,
    string ReferenceTypeDisplay,
    string MismatchedColumnName,
    string MismatchedTypeDisplay,
    [property: JsonIgnore] string SourcePath,
    [property: JsonIgnore] int Line,
    [property: JsonIgnore] int Column,
    FindingConfidence Confidence = FindingConfidence.High) : IFinding
{
    public string RuleId { get; } = FindingRuleIds.UnpivotExactTypeMismatchRuleId;

    public SourceSpan Location => new(SourcePath, Line, Column);
}
