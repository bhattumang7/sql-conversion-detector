using SilentScan.Core.Predicates;

namespace SilentScan.Verify.Oracle;

public sealed record ExpressionDerivedResult(
    ExpressionDerivedFinding Finding,
    ExpressionDerivedOutcome Outcome,
    string? Detail);
