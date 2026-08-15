using SilentScan.Core.Predicates;

namespace SilentScan.Verify.Oracle;

/// <summary>One TVF-fence finding's oracle-probe outcome, carrying enough of the original finding to report it.</summary>
public sealed record TvfFenceResult(
    TvfFenceFinding Finding,
    TvfFenceOutcome Outcome,
    string? Detail);
