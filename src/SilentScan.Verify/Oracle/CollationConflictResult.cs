using SilentScan.Core.Predicates;

namespace SilentScan.Verify.Oracle;

public sealed record CollationConflictResult(
    CollationConflictFinding Finding,
    CollationConflictOutcome Outcome,
    string? Detail);
