using SilentScan.Core.Predicates;

namespace SilentScan.Verify.Oracle;

public sealed record ScalarUdfResult(
    ScalarUdfFinding Finding,
    ScalarUdfOutcome Outcome,
    string? Detail);
