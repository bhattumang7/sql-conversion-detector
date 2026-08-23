using SilentScan.Core.Predicates;

namespace SilentScan.Verify.Oracle;

public sealed record TvfFenceResult(
    TvfFenceFinding Finding,
    TvfFenceOutcome Outcome,
    string? Detail);
