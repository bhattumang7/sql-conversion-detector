using SilentScan.Core.Predicates;
using SilentScan.Core.Reporting.Sarif;

namespace SilentScan.Core.Reporting.RuleDocs.ControlFlow;

internal static class DirtyReadIsolationHint
{
    public static string RuleId => SarifRuleCatalog.ControlFlowRiskRuleId(ControlFlowRiskFindingKind.DirtyReadIsolationHint);

    public static RuleDocContent Content { get; } = new(
        WhyItMatters: """
            A `NOLOCK`/`READUNCOMMITTED` table hint, or `SET TRANSACTION ISOLATION LEVEL READ
            UNCOMMITTED`, allows dirty reads - seeing uncommitted data from another transaction that
            might still be rolled back. Less widely known: it can also silently miss rows or
            double-count rows entirely, as a side effect of reading pages mid-split during a
            concurrent page split, independent of whether any transaction involved ever rolls back.

            This is reported as advisory, not an error: for a reporting or analytics workload where
            approximate, momentarily-stale results are an acceptable, deliberate tradeoff for
            avoiding lock contention, this is a completely reasonable choice, not a default-bad one.
            Reported at Low confidence for that reason - a flag worth a second look in review, not a
            claim that the hint is wrong wherever it appears.
            """,
        Examples:
        [
            new RuleDocExample(
                Title: "A NOLOCK hint on a query whose correctness matters",
                NoncompliantSql: """
                    SELECT Balance
                    FROM dbo.Accounts WITH (NOLOCK)
                    WHERE Id = @AccountId;
                    """,
                NoncompliantExplanation: "This query can return an uncommitted, possibly-about-to-be-rolled-back balance, or (during a concurrent page split) silently miss or double-count rows - worth confirming this is deliberate if the result feeds anything where accuracy matters, like a financial balance check.",
                CompliantSql: """
                    SELECT Balance
                    FROM dbo.Accounts
                    WHERE Id = @AccountId;
                    """,
                CompliantExplanation: "Without the hint, the query reads under the session's normal isolation level, seeing only committed data."),
        ]);
}
