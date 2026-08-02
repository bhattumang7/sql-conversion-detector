using SilentScan.Core.Predicates;

namespace SilentScan.Verify.Oracle;

/// <summary>One collation-conflict finding's oracle-probe outcome, carrying enough of the original finding to report it.</summary>
public sealed record CollationConflictResult(
    CollationConflictFinding Finding,
    CollationConflictOutcome Outcome,
    string? Detail);
