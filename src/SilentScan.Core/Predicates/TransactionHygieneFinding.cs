
using SilentScan.Core.Rules;
namespace SilentScan.Core.Predicates;

public enum TransactionHygieneFindingKind
{
    UnresolvedOnSomePath,
    ImplicitTransactionUnresolvedOnSomePath,
    CommitAfterXactAbortDoomsTransaction,
}

public sealed record TransactionHygieneFinding(
    TransactionHygieneFindingKind Kind,
    string SourcePath,
    int BeginTransactionLine,
    int BeginTransactionColumn,
    int UnresolvedExitLine,
    int UnresolvedExitColumn,
    FindingConfidence Confidence = FindingConfidence.High) : IFinding
{
    public string RuleId { get; } = FindingRuleIds.TransactionHygieneRuleId(Kind);

    public SourceSpan Location => new(SourcePath, BeginTransactionLine, BeginTransactionColumn);
}
