namespace SilentScan.Core.Predicates;

public enum TransactionHygieneFindingKind
{
UnresolvedOnSomePath,
}

public sealed record TransactionHygieneFinding(
    TransactionHygieneFindingKind Kind,
    string SourcePath,
    int BeginTransactionLine,
    int BeginTransactionColumn,
    int UnresolvedExitLine,
    int UnresolvedExitColumn,
    FindingConfidence Confidence = FindingConfidence.High);
