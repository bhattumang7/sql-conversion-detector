using System.Text.Json.Serialization;
using SilentScan.Core.Rules;

namespace SilentScan.Core.Predicates;

public enum IndexHintFindingKind
{
    IndexDoesNotExist,

    HintedIndexNotSeekable,
}

public sealed record IndexHintFinding(
    IndexHintFindingKind Kind,
    string TableQualifiedName,
    string HintedIndexName,
    string? LeadingColumnName,
    [property: JsonIgnore] string SourcePath,
    [property: JsonIgnore] int Line,
    [property: JsonIgnore] int Column,
    FindingConfidence Confidence = FindingConfidence.High) : IFinding
{
    public string RuleId { get; } = FindingRuleIds.IndexHintRuleId(Kind);

    public SourceSpan Location => new(SourcePath, Line, Column);
}

