namespace SilentScan.Verify.Oracle;

/// <summary>Result of oracle-probing a single <see cref="Core.Predicates.SargabilityFinding"/> against the real engine.</summary>
public enum Tier1Outcome
{
    /// <summary>An index exists (corpus-declared) with this column as its leading key, and the plan shows no Index Seek at all - the syntactic wrap really did defeat the seek an index would otherwise have offered.</summary>
    Confirmed,

    /// <summary>The wrap did NOT defeat the seek - the plan shows an Index Seek despite it, contradicting the finding's own claim.</summary>
    NotConfirmed,

    /// <summary>The finding lacked enough information to synthesize a probe (no rendered fragment, no resolved table, or no resolvable column type).</summary>
    NotProbeable,

    /// <summary>The probe failed to compile/execute against the deployed schema.</summary>
    ProbeFailed,

    /// <summary>The corpus's own DDL never indexed this column, and no scratch index could be deployed for it either (e.g. an unindexable column type) - there is no seek to have lost in the first place, so the plan-shape signal cannot distinguish this from an ordinary heap scan.</summary>
    ConfirmedUnindexed,

    /// <summary>Mirrors <see cref="CorpusFindingOutcome.ConfirmedViaScratchIndex"/>: the corpus's own DDL never indexed this column, but the plan-shape claim was still confirmed against a scratch index deployed for this probe only.</summary>
    ConfirmedViaScratchIndex,
}
