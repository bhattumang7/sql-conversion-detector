using SilentScan.Core.Predicates;

namespace SilentScan.Verify.Oracle;

public sealed record Tier1Result(
    SargabilityFinding Finding,
    Tier1Outcome Outcome,
    string? Detail);
