using SilentScan.Core.Predicates;

namespace SilentScan.Verify.Oracle;

public sealed record CorpusFindingResult(
    TypedPredicateFinding Finding,
    CorpusFindingOutcome Outcome,
    string? Detail);
