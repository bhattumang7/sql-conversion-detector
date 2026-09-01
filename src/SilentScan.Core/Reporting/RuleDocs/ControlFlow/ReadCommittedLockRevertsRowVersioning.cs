using SilentScan.Core.Predicates;
using SilentScan.Core.Reporting.Sarif;

namespace SilentScan.Core.Reporting.RuleDocs.ControlFlow;

internal static class ReadCommittedLockRevertsRowVersioning
{
    public static string RuleId => SarifRuleCatalog.ControlFlowRiskRuleId(ControlFlowRiskFindingKind.ReadCommittedLockRevertsRowVersioning);

    public static RuleDocContent Content { get; } = new(
        WhyItMatters: """
            When a database has `READ_COMMITTED_SNAPSHOT` turned on, every ordinary `READ COMMITTED`
            read in that database becomes row-versioned: readers see a transactionally consistent
            snapshot and never block writers (or get blocked by them). A `READCOMMITTEDLOCK` table
            hint opts a single table reference back out of that - it forces the pre-snapshot,
            share-lock-based `READ COMMITTED` behavior for just that one reference, silently
            reintroducing the blocking/deadlock risk the rest of the batch no longer has.

            Nothing at the query site signals this reversion: the hint reads like an ordinary,
            conservative locking choice, and is easy to miss as changing behavior specifically
            *because* the database has row versioning turned on. This is only reported when this
            tool can confirm, from the target database's own catalog, that
            `READ_COMMITTED_SNAPSHOT` is actually on - on a database where it is off,
            `READCOMMITTEDLOCK` is a no-op restating the database's own default behavior.
            """,
        Examples:
        [
            new RuleDocExample(
                Title: "A READCOMMITTEDLOCK hint on a database with row versioning on",
                NoncompliantSql: """
                    SELECT Balance
                    FROM dbo.Accounts WITH (READCOMMITTEDLOCK)
                    WHERE Id = @AccountId;
                    """,
                NoncompliantExplanation: "On a database with READ_COMMITTED_SNAPSHOT ON, this reference alone reverts to blocking/locking reads while every other read in the batch stays row-versioned and non-blocking.",
                CompliantSql: """
                    SELECT Balance
                    FROM dbo.Accounts
                    WHERE Id = @AccountId;
                    """,
                CompliantExplanation: "Without the hint, this reference reads under the database's own row-versioned READ COMMITTED behavior, consistent with the rest of the batch."),
        ]);
}
