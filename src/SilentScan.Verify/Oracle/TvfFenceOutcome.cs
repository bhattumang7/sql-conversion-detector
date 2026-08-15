namespace SilentScan.Verify.Oracle;

/// <summary>
/// Result of oracle-probing a single <see cref="Core.Predicates.TvfFenceFinding"/> against the
/// real engine. The marker (docs/detection-checklist.md, oracle-verified directly against the
/// local Docker instance): a multi-statement/CLR TVF reference produces a
/// <c>PhysicalOp="Table-valued function"</c> RelOp with the fixed cardinality guess as its own
/// <c>EstimateRows</c>; an inline TVF reference dissolves into ordinary base operators and never
/// produces that node at all. <c>INSERT ... EXEC</c> has its own marker,
/// <c>StatementType="INSERT EXEC"</c>.
/// </summary>
public enum TvfFenceOutcome
{
    /// <summary>The plan contains the fence marker for this finding's own kind - the claim held against the real engine.</summary>
    Confirmed,

    /// <summary>The plan does NOT contain the fence marker - contradicts the finding's own claim (would indicate a catalog misclassification: the referenced object was not actually a multi-statement/CLR TVF).</summary>
    NotConfirmed,

    /// <summary>The finding lacked enough information to synthesize a probe (an unresolvable function/procedure name, a parameter type this tool cannot render, or - for INSERT...EXEC - a result shape the engine itself could not describe).</summary>
    NotProbeable,

    /// <summary>The probe failed to compile/execute against the deployed schema.</summary>
    ProbeFailed,
}
