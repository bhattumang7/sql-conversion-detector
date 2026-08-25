using System.Text.Json.Serialization;
using SilentScan.Core.Rules;

namespace SilentScan.Core.Predicates;

public sealed record CompositeIndexLeadingColumnFinding(
    string TableQualifiedName,
    string? IndexName,
    IReadOnlyList<string> IndexKeyColumns,
    string ViolatingColumnName,
    int ViolatingColumnPosition,
    [property: JsonIgnore] string SourcePath,
    [property: JsonIgnore] int Line,
    [property: JsonIgnore] int Column,
    FindingConfidence Confidence = FindingConfidence.High) : IFinding
{
    public string RuleId { get; } = FindingRuleIds.CompositeIndexLeadingColumnRuleId;

    public SourceSpan Location => new(SourcePath, Line, Column);
}

