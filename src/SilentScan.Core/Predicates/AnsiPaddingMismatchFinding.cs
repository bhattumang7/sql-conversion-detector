using System.Text.Json.Serialization;
using SilentScan.Core.Rules;

namespace SilentScan.Core.Predicates;

public sealed record AnsiPaddingMismatchFinding(
    string TableQualifiedName,
    string ColumnName,
    string PatternLiteralText,
    [property: JsonIgnore] string SourcePath,
    [property: JsonIgnore] int Line,
    [property: JsonIgnore] int Column,
    FindingConfidence Confidence = FindingConfidence.High) : IFinding
{
    public string RuleId { get; } = FindingRuleIds.AnsiPaddingMismatchRuleId;

    public SourceSpan Location => new(SourcePath, Line, Column);
}

