using System.Text.Json.Serialization;
using SilentScan.Core.Rules;

namespace SilentScan.Core.Predicates;

public sealed record SecurityPredicateIndexFinding(
    string PolicyQualifiedName,
    string TableQualifiedName,
    string PredicateFunctionQualifiedName,
    IReadOnlyList<string> FilteredColumns,
    [property: JsonIgnore] string SourcePath,
    [property: JsonIgnore] int Line,
    FindingConfidence Confidence = FindingConfidence.Medium) : IFinding
{
    public string RuleId { get; } = FindingRuleIds.SecurityPredicateIndexRuleId;

    public SourceSpan Location => new(SourcePath, Line, 1);
}

