using System.Text.Json.Serialization;
using SilentScan.Core.Rules;

namespace SilentScan.Core.Predicates;

public sealed record FilteredIndexParameterMismatchFinding(
    string TableQualifiedName,
    string ColumnName,
    string? IndexName,
    string FilterLiteralText,
    string VariableName,
    bool IsFormalParameter,
    string Operator,
    [property: JsonIgnore] string SourcePath,
    [property: JsonIgnore] int Line,
    [property: JsonIgnore] int Column,
    FindingConfidence Confidence = FindingConfidence.High) : IFinding
{
    public string RuleId { get; } = FindingRuleIds.FilteredIndexParameterMismatchRuleId;

    public SourceSpan Location => new(SourcePath, Line, Column);
}

