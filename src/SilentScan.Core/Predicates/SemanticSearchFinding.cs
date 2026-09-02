using System.Text.Json.Serialization;
using SilentScan.Core.Rules;

namespace SilentScan.Core.Predicates;

public enum SemanticSearchFindingKind
{
    TableNotSemanticFullTextIndexed,

    ColumnNotSemanticFullTextIndexed,
}

public sealed record SemanticSearchFinding(
    SemanticSearchFindingKind Kind,
    string TableQualifiedName,
    string? ColumnName,
    string Detail,
    [property: JsonIgnore] string SourcePath,
    [property: JsonIgnore] int Line,
    [property: JsonIgnore] int Column,
    FindingConfidence Confidence = FindingConfidence.High) : IFinding
{
    public string RuleId { get; } = FindingRuleIds.SemanticSearchRuleId(Kind);

    public SourceSpan Location => new(SourcePath, Line, Column);
}
