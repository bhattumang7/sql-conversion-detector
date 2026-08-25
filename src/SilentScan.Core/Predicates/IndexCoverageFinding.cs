using System.Text.Json.Serialization;
using SilentScan.Core.Rules;

namespace SilentScan.Core.Predicates;

public enum IndexCoverageFindingKind
{
    KeyLookupProneIndex,
}

public sealed record IndexCoverageFinding(
    IndexCoverageFindingKind Kind,
    string TableQualifiedName,
    string? IndexName,
    IReadOnlyList<string> IndexKeyColumns,
    IReadOnlyList<string> IndexIncludedColumns,
    IReadOnlyList<string> UncoveredColumns,
    [property: JsonIgnore] string SourcePath,
    [property: JsonIgnore] int Line,
    [property: JsonIgnore] int Column,
    FindingConfidence Confidence = FindingConfidence.High) : IFinding
{
    public string RuleId { get; } = FindingRuleIds.IndexCoverageRuleId(Kind);

    public SourceSpan Location => new(SourcePath, Line, Column);
}

