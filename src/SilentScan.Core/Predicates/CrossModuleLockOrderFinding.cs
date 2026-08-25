
using SilentScan.Core.Rules;
namespace SilentScan.Core.Predicates;

public sealed record LockOrderProcedureSite(
    string ProcedureQualifiedName, string SourcePath, int ProcedureLine, int FirstWriteLine, int SecondWriteLine);

public sealed record CrossModuleLockOrderFinding(
    string FirstTableQualifiedName,
    string SecondTableQualifiedName,
    LockOrderProcedureSite FirstTableFirstOrdering,
    LockOrderProcedureSite SecondTableFirstOrdering,
    FindingConfidence Confidence = FindingConfidence.Medium) : IFinding
{
    public string RuleId { get; } = FindingRuleIds.CrossModuleLockOrderRuleId;

    public SourceSpan Location => new(FirstTableFirstOrdering.SourcePath, FirstTableFirstOrdering.ProcedureLine, 1);
}
