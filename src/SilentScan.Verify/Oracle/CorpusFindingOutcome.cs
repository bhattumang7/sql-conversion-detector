namespace SilentScan.Verify.Oracle;

/// <summary>Result of oracle-probing a single corpus finding against the real engine (CLAUDE.md Verify workflow).</summary>
public enum CorpusFindingOutcome
{
    /// <summary>The plan showed CONVERT_IMPLICIT on the finding's own column - the finding is engine-confirmed.</summary>
    Confirmed,

    /// <summary>The probe compiled cleanly but showed no column-side conversion - the static verdict was a false positive.</summary>
    NotConfirmed,

    /// <summary>The finding's operand types weren't enough to synthesize a probe (e.g. an unresolvable other-side type).</summary>
    NotProbeable,

    /// <summary>The probe failed to compile/execute against the deployed schema (e.g. the column no longer matches the deployed DDL).</summary>
    ProbeFailed,
}
