using SilentScan.Core.Predicates;

namespace SilentScan.Verify.Oracle;

/// <summary>One expression-derived finding's oracle-probe outcome, carrying enough of the original finding to report it.</summary>
public sealed record ExpressionDerivedResult(
    ExpressionDerivedFinding Finding,
    ExpressionDerivedOutcome Outcome,
    string? Detail);
