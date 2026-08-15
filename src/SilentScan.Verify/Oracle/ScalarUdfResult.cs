using SilentScan.Core.Predicates;

namespace SilentScan.Verify.Oracle;

/// <summary>One scalar-UDF finding's oracle-probe outcome, carrying enough of the original finding to report it.</summary>
public sealed record ScalarUdfResult(
    ScalarUdfFinding Finding,
    ScalarUdfOutcome Outcome,
    string? Detail);
