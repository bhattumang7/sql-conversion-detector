using System.Text.Json.Serialization;
using SilentScan.Core.Rules;

namespace SilentScan.Core.Predicates;

public sealed record AggregateDivisionColumnstoreFinding(
    string AggregateFunctionName,
    string TableQualifiedName,
    [property: JsonIgnore] string SourcePath,
    [property: JsonIgnore] int Line,
    [property: JsonIgnore] int Column,
    FindingConfidence Confidence = FindingConfidence.Low) : IFinding
{
    public string RuleId { get; } = FindingRuleIds.AggregateDivisionColumnstoreRuleId;

    public SourceSpan Location => new(SourcePath, Line, Column);
}

