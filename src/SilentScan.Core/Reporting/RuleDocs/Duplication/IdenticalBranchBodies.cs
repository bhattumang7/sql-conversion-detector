using SilentScan.Core.Predicates;
using SilentScan.Core.Reporting.Sarif;

namespace SilentScan.Core.Reporting.RuleDocs.Duplication;

internal static class IdenticalBranchBodies
{
    public static string RuleId => SarifRuleCatalog.DuplicationRuleId(DuplicationFindingKind.IdenticalBranchBodies);

    public static RuleDocContent Content { get; } = new(
        WhyItMatters: """
            Two (but not all) branches of a conditional structure have an identical body or result -
            either the conditional is partly pointless, or a copy-paste mistake left one branch
            matching another when it should have differed.
            """,
        Examples:
        [
            new RuleDocExample(
                Title: "Two CASE branches with the identical result",
                NoncompliantSql: """
                    SELECT OrderId,
                        CASE
                            WHEN Status = 'Active' THEN 'Open'
                            WHEN Status = 'OnHold' THEN 'Open'
                            WHEN Status = 'Cancelled' THEN 'Closed'
                            ELSE 'Unknown'
                        END AS DisplayStatus
                    FROM dbo.Orders;
                    """,
                NoncompliantExplanation: "The 'Active' and 'OnHold' branches both produce 'Open' - either intentional and worth combining, or one branch was meant to produce something else.",
                CompliantSql: """
                    SELECT OrderId,
                        CASE
                            WHEN Status IN ('Active', 'OnHold') THEN 'Open'
                            WHEN Status = 'Cancelled' THEN 'Closed'
                            ELSE 'Unknown'
                        END AS DisplayStatus
                    FROM dbo.Orders;
                    """,
                CompliantExplanation: "Combining the two statuses into one IN condition makes the shared 'Open' result an explicit, intentional grouping."),
        ]);
}
