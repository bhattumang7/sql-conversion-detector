using SilentScan.Core.Predicates;
using SilentScan.Core.Reporting.Sarif;

namespace SilentScan.Core.Reporting.RuleDocs.QueryAntiPattern;

internal static class MergeUnconditionalDelete
{
    public static string RuleId => SarifRuleCatalog.QueryAntiPatternRuleId(QueryAntiPatternFindingKind.MergeUnconditionalDelete);

    public static RuleDocContent Content { get; } = new(
        WhyItMatters: """
            MERGE lets a WHEN MATCHED or WHEN NOT MATCHED BY SOURCE branch carry its own extra
            `AND` condition beyond the base match/no-match test - `WHEN MATCHED AND
            source.Status = 'Cancelled' THEN DELETE`, for instance - narrowing exactly which
            matched (or unmatched-by-source) rows the branch applies to. When a DELETE branch omits
            that extra condition entirely, it deletes every single target row that satisfies the
            bare match/no-match test, with nothing else scoping it down.

            This matters most for WHEN NOT MATCHED BY SOURCE THEN DELETE, which is specifically the
            branch that handles target rows the USING source doesn't mention at all - it exists to
            support incremental-sync patterns where the source represents "the current full state"
            and any target row absent from it should be removed. That's exactly the branch where
            an unconditional DELETE is most dangerous: if the USING source was ever built with an
            unintended filter, a bad join, an empty result from an upstream failure, or simply
            scoped more narrowly than the author assumed, every target row that source doesn't
            happen to include - which, in a badly-scoped-source scenario, can be most or all of the
            table - is deleted, turning what was meant to be a small incremental sync into a
            mass-delete of the entire target table. The same risk, though usually smaller in blast
            radius, applies to an unconditional WHEN MATCHED THEN DELETE: every row that merely
            matches the ON clause gets deleted regardless of any other property of that row.

            Because this is a single statement executed atomically, there's no intermediate
            checkpoint where the scope of the delete can be inspected before it commits - the whole
            table's worth of deletes happens in the same transaction as everything else the MERGE
            does, which is exactly why the branch's own scoping condition is the only thing standing
            between "sync a handful of rows" and "delete the table."
            """,
        HowToFixIt: """
            Add an explicit `AND` condition to the DELETE branch that scopes it to the rows that are
            actually meant to be removed, rather than relying on the bare match/no-match test alone.
            For WHEN NOT MATCHED BY SOURCE in particular, this is often a condition that also
            narrows which target rows are even eligible to be considered "in sync" with the source
            in the first place (a status flag, a date range, a partition key) so an unexpectedly
            narrow or empty source can't accidentally delete rows that were never in the source's
            intended scope to begin with.
            """,
        Examples:
        [
            new RuleDocExample(
                Title: "WHEN NOT MATCHED BY SOURCE THEN DELETE with no scoping condition",
                NoncompliantSql: """
                    CREATE TABLE dbo.ActiveSubscriptions (SubscriptionId INT NOT NULL PRIMARY KEY, CustomerId INT NOT NULL);
                    CREATE TABLE dbo.StagingSubscriptions (SubscriptionId INT NOT NULL PRIMARY KEY, CustomerId INT NOT NULL);

                    MERGE dbo.ActiveSubscriptions AS target
                    USING dbo.StagingSubscriptions AS source
                        ON target.SubscriptionId = source.SubscriptionId
                    WHEN NOT MATCHED BY SOURCE THEN
                        DELETE;
                    """,
                NoncompliantExplanation: "If StagingSubscriptions is ever loaded empty or scoped more narrowly than intended (an upstream extract failure, a bad filter), every row in ActiveSubscriptions is 'not matched by source' and this branch deletes the entire table in one statement.",
                CompliantSql: """
                    MERGE dbo.ActiveSubscriptions AS target
                    USING dbo.StagingSubscriptions AS source
                        ON target.SubscriptionId = source.SubscriptionId
                    WHEN NOT MATCHED BY SOURCE AND target.CustomerId IS NOT NULL THEN
                        DELETE;
                    """,
                CompliantExplanation: "An explicit scoping condition on the DELETE branch means an unexpectedly empty or narrow source can only ever affect the rows the condition allows, instead of every row the bare match test leaves unmatched."),
        ]);
}
