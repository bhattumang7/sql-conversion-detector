using System.Text.Json.Serialization;
using SilentScan.Core.Rules;

namespace SilentScan.Core.Predicates;

public enum ComputedColumnIndexKeyFindingKind
{
    NonDeterministic,

    Imprecise,
}

public sealed record ComputedColumnIndexKeyFinding(
    ComputedColumnIndexKeyFindingKind Kind,
    string TableQualifiedName,
    string ColumnName,
    string? IndexName,
    [property: JsonIgnore] string SourcePath,
    [property: JsonIgnore] int Line,
    FindingConfidence Confidence = FindingConfidence.High) : IFinding
{
    public string RuleId { get; } = FindingRuleIds.ComputedColumnIndexKeyRuleId(Kind);

    public SourceSpan Location => new(SourcePath, Line, 1);
}
