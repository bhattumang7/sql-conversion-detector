using SilentScan.Core.Predicates;
using SilentScan.Core.Reporting.Sarif;

namespace SilentScan.Core.Reporting.RuleDocs.Duplication;

internal static class DuplicateSiblingCondition
{
    public static string RuleId => SarifRuleCatalog.DuplicationRuleId(DuplicationFindingKind.DuplicateSiblingCondition);

    public static RuleDocContent Content { get; } = new(
        WhyItMatters: """
            A later branch in an IF/ELSE IF chain or CASE expression repeats an earlier sibling's own
            condition verbatim - the later branch can never be reached. Whatever logic that later
            branch holds silently never runs.
            """,
        HowToFixIt: "Remove the later branch - it can never be reached - or fix its condition if a different check was intended.",
        Examples:
        [
            new RuleDocExample(
                Title: "A CASE branch repeating an earlier branch's condition",
                NoncompliantSql: """
                    SELECT OrderId,
                        CASE
                            WHEN Status = 'Active' THEN 'A'
                            WHEN Status = 'Cancelled' THEN 'C'
                            WHEN Status = 'Active' THEN 'X'
                            ELSE 'U'
                        END AS StatusCode
                    FROM dbo.Orders;
                    """,
                NoncompliantExplanation: "The third branch repeats the first branch's Status = 'Active' condition verbatim, so it can never be reached.",
                CompliantSql: """
                    SELECT OrderId,
                        CASE
                            WHEN Status = 'Active' THEN 'A'
                            WHEN Status = 'Cancelled' THEN 'C'
                            WHEN Status = 'OnHold' THEN 'X'
                            ELSE 'U'
                        END AS StatusCode
                    FROM dbo.Orders;
                    """,
                CompliantExplanation: "The third branch now checks a distinct status, so it is actually reachable."),
        ]);
}
