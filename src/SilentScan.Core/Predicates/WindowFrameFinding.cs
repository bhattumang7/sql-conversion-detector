using System.Text.Json.Serialization;
using SilentScan.Core.Rules;


namespace SilentScan.Core.Predicates;

public enum WindowFrameFindingKind
{
    ExplicitRangeFrame,

    ImplicitDefaultRangeFrame,
}

public sealed record WindowFrameFinding(
    WindowFrameFindingKind Kind,
    [property: JsonIgnore] string SourcePath,
    [property: JsonIgnore] int Line,
    [property: JsonIgnore] int Column,
    FindingConfidence Confidence = FindingConfidence.High) : IFinding
{
    public string RuleId { get; } = FindingRuleIds.WindowFrameRuleId(Kind);

    public SourceSpan Location => new(SourcePath, Line, Column);
}

