using SilentScan.Core.Predicates;

namespace SilentScan.Verify.Oracle;

/// <summary>One Tier-1 finding's oracle-probe outcome, carrying enough of the original finding to report it.</summary>
public sealed record Tier1Result(
    SargabilityFinding Finding,
    Tier1Outcome Outcome,
    string? Detail);
