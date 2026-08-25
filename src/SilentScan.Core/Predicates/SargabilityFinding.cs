using System.Text.Json.Serialization;
using SilentScan.Core.Common;
using SilentScan.Core.Rules;

namespace SilentScan.Core.Predicates;

public sealed record SargabilityFinding(
    SargabilityFindingKind Kind,
    string ColumnName,
    string? Detail,
    [property: JsonIgnore] string SourcePath,
    [property: JsonIgnore] int Line,
    [property: JsonIgnore] int Column,
    SourceSpan? DynamicSqlCallSite = null,
    string? TableQualifiedName = null,
    bool? Indexed = null,
    string? PredicateFragmentText = null,
    FindingConfidence Confidence = FindingConfidence.High) : IRelocatableFinding<SargabilityFinding>, IFinding
{
    public string RuleId { get; } = FindingRuleIds.Tier1RuleId(Kind);

    public SourceSpan Location => new(SourcePath, Line, Column);
    int IRelocatableFinding<SargabilityFinding>.PositionColumn => Column;

    SargabilityFinding IRelocatableFinding<SargabilityFinding>.Relocated(SourceSpan span, SourceSpan? callSite, FindingConfidence confidence) =>
        this with { SourcePath = span.SourcePath, Line = span.Line, Column = span.Column, DynamicSqlCallSite = callSite, Confidence = confidence };
}
