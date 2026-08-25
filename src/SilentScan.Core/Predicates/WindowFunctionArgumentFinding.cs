using System.Text.Json.Serialization;
using SilentScan.Core.Rules;


namespace SilentScan.Core.Predicates;

public enum WindowFunctionArgumentFindingKind
{
    LagLeadNegativeOffset,
    PercentileOutOfRange,
}

public sealed record WindowFunctionArgumentFinding(
    WindowFunctionArgumentFindingKind Kind,
    string FunctionName,
    string ArgumentText,
    [property: JsonIgnore] string SourcePath,
    [property: JsonIgnore] int Line,
    [property: JsonIgnore] int Column,
    FindingConfidence Confidence = FindingConfidence.High) : IFinding
{
    public string RuleId { get; } = FindingRuleIds.WindowFunctionArgumentRuleId(Kind);

    public SourceSpan Location => new(SourcePath, Line, Column);
}
