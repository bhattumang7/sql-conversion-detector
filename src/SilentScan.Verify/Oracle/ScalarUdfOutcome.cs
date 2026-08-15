namespace SilentScan.Verify.Oracle;

/// <summary>
/// Result of oracle-probing a single <see cref="Core.Predicates.ScalarUdfFinding"/> against the
/// real engine (docs/detection-checklist.md Tier 1 #1). Marker, oracle-verified directly against
/// the local Docker instance: a scalar UDF call that is NOT folded away by the engine produces a
/// <c>&lt;UserDefinedFunction FunctionName="..."&gt;</c> plan element; one the engine fully
/// inlines (SQL 2019+ FROID) instead produces <c>ContainsInlineScalarTsqlUdfs="1"</c> on the
/// enclosing <c>StmtSimple</c> and no <c>UserDefinedFunction</c> element at all. See
/// <see cref="ScalarUdfVerifier"/>'s own doc comment for the two-probe design this drives.
/// </summary>
public enum ScalarUdfOutcome
{
    /// <summary>The pinned probe confirms the function reference, and the natural probe's inlining behavior does not contradict the finding's own <see cref="Core.Predicates.ScalarUdfInlineability"/> read.</summary>
    Confirmed,

    /// <summary>Either the pinned probe shows no scalar-UDF plan element at all (contradicts the finding's own claim that this is a scalar UDF reference), or the natural probe's inlining behavior contradicts the finding's own Inlineability read.</summary>
    NotConfirmed,

    /// <summary>The finding lacked enough information to synthesize a probe (an unresolvable function name, or a parameter type this tool cannot render), or is a SchemaDependency finding, which is catalog-definitive and never probed.</summary>
    NotProbeable,

    /// <summary>The probe failed to compile/execute against the deployed schema.</summary>
    ProbeFailed,
}
