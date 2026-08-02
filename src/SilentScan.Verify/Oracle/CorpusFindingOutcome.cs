namespace SilentScan.Verify.Oracle;

/// <summary>Result of oracle-probing a single corpus finding against the real engine (CLAUDE.md Verify workflow).</summary>
public enum CorpusFindingOutcome
{
    /// <summary>
    /// The plan showed CONVERT_IMPLICIT on the finding's own column, and - for a RangeSeek or
    /// ScanForced finding - the plan's seek/scan shape matches what that verdict predicts
    /// (docs/audit-remediation-plan.md Phase 5.1, audit finding C1: both verdicts produce a
    /// column-side convert, so conversion presence alone can't tell them apart).
    /// </summary>
    Confirmed,

    /// <summary>
    /// The probe compiled cleanly but showed no column-side conversion, or - for a RangeSeek/
    /// ScanForced finding - showed one with the wrong plan shape for that verdict.
    /// </summary>
    NotConfirmed,

    /// <summary>The finding's operand types weren't enough to synthesize a probe (e.g. an unresolvable other-side type).</summary>
    NotProbeable,

    /// <summary>The probe failed to compile/execute against the deployed schema (e.g. the column no longer matches the deployed DDL).</summary>
    ProbeFailed,

    /// <summary>
    /// The plan confirmed the finding's core claim - CONVERT_IMPLICIT lands on the column, which
    /// is provable regardless of indexing - but the column has no deployed leading-key index, so
    /// the RangeSeek-vs-ScanForced shape distinction (which depends on an index existing to seek
    /// or scan through) could not be checked. The probe always runs first regardless of whether
    /// an index deployed (an audit finding: gating the probe itself behind an index check meant
    /// a column that is genuinely unindexed by the corpus's own DDL - the common case, not the
    /// exception - produced no oracle signal at all, when the conversion claim itself was fully
    /// provable without one). A missing/undeployed table or column instead fails the probe
    /// itself and reports <see cref="ProbeFailed"/>, not this.
    /// </summary>
    ConfirmedUnindexed,
}
