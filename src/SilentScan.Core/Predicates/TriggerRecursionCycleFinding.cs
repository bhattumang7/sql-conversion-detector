
using SilentScan.Core.Rules;
namespace SilentScan.Core.Predicates;

public sealed record TriggerRecursionCycleHop(
    string TriggerQualifiedName, string SourcePath, int TriggerLine,
    string FromTableQualifiedName, string ToTableQualifiedName, int WriteLine);

public sealed record TriggerRecursionCycleFinding(
    IReadOnlyList<string> CycleTableQualifiedNames,
    IReadOnlyList<TriggerRecursionCycleHop> Hops,
    FindingConfidence Confidence = FindingConfidence.Medium) : IFinding
{
    public string RuleId { get; } = FindingRuleIds.TriggerRecursionCycleRuleId;

    public SourceSpan Location => Hops is [{ } first, ..]
        ? new(first.SourcePath, first.TriggerLine, 1)
        : new(string.Empty, 0, 0);
}
