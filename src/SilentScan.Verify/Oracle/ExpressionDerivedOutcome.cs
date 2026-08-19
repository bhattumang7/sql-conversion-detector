namespace SilentScan.Verify.Oracle;

/// <summary>Result of oracle-probing a single <see cref="Core.Predicates.ExpressionDerivedFinding"/> against the real engine.</summary>
public enum ExpressionDerivedOutcome
{
    /// <summary>At least one underlying base column is indexed, and the plan shows no Index Seek at all - the expression really did make the seek unavailable.</summary>
    Confirmed,

    /// <summary>The plan showed an Index Seek despite the expression-derived column, contradicting the finding's own claim.</summary>
    NotConfirmed,

    /// <summary>No rendered predicate fragment, or the column came from an inline derived table/CTE rather than a real catalog view/TVF - a probe has no standalone object to target.</summary>
    NotProbeable,

    /// <summary>The probe failed to compile/execute against the deployed schema.</summary>
    ProbeFailed,

    /// <summary>No underlying base column is indexed at all - there is no seek to have lost, so no plan was ever captured and nothing was confirmed. Unlike <see cref="Tier1Outcome.UnindexedNotProbeable"/>/<see cref="CorpusFindingOutcome.ConfirmedUnindexed"/>, this does not yet fall back to a scratch index - a documented, smaller follow-up gap (an expression-derived finding can have several underlying base columns across several tables, unlike the single-column case those two already handle).</summary>
    UnindexedNotProbeable,
}
