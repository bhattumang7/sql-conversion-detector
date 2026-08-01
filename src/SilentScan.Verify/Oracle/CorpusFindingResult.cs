using SilentScan.Core.Predicates;

namespace SilentScan.Verify.Oracle;

/// <summary>One finding's oracle-probe outcome, carrying enough of the original finding to report it (CLAUDE.md: "Study reports only oracle-confirmed findings; static-only findings go in an appendix").</summary>
public sealed record CorpusFindingResult(
    TypedPredicateFinding Finding,
    CorpusFindingOutcome Outcome,
    string? Detail);
