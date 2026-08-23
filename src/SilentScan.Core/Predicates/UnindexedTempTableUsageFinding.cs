namespace SilentScan.Core.Predicates;

public enum UnindexedTempTableUsageKind
{
    JoinOperand,
    FilteredInWhere,
}

public sealed record UnindexedTempTableUsageFinding(
    UnindexedTempTableUsageKind Kind,
    string TempTableQualifiedName,
    string SourcePath,
    int DeclarationLine,
    int UsageLine,
    int UsageColumn,
    FindingConfidence Confidence = FindingConfidence.Medium);
