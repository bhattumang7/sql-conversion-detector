using System.Text.Json.Serialization;
using SilentScan.Core.Rules;

namespace SilentScan.Core.Predicates;

public enum GroupByValidityFindingKind
{
    SelectList,
    Having,
    OrderBy,
}

public sealed record GroupByValidityFinding(
    GroupByValidityFindingKind Kind,
    string ExpressionText,
    [property: JsonIgnore] string SourcePath,
    [property: JsonIgnore] int Line,
    [property: JsonIgnore] int Column,
    FindingConfidence Confidence = FindingConfidence.High) : IFinding
{
    public string RuleId { get; } = FindingRuleIds.GroupByValidityRuleId(Kind);

    public SourceSpan Location => new(SourcePath, Line, Column);
}
