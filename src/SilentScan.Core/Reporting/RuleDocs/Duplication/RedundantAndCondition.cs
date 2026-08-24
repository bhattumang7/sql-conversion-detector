using SilentScan.Core.Predicates;
using SilentScan.Core.Reporting.Sarif;

namespace SilentScan.Core.Reporting.RuleDocs.Duplication;

internal static class RedundantAndCondition
{
    public static string RuleId => SarifRuleCatalog.DuplicationRuleId(DuplicationFindingKind.RedundantAndCondition);

    public static RuleDocContent Content { get; } = new(
        WhyItMatters: """
            Two conjuncts of one AND-combined condition compare the same operand against numeric
            bounds where one bound's range is already a subset of the other's - the looser bound adds
            nothing. Keeping both makes the actual effective range harder to see at a glance.
            """,
        HowToFixIt: "Drop the looser numeric bound - it adds nothing since the other conjunct's range is already a subset.",
        Examples:
        [
            new RuleDocExample(
                Title: "A redundant looser numeric bound",
                NoncompliantSql: "SELECT OrderId FROM dbo.Orders WHERE Quantity > 0 AND Quantity > 10;",
                NoncompliantExplanation: "Quantity > 0 is already implied whenever Quantity > 10 holds, so the first condition adds nothing.",
                CompliantSql: "SELECT OrderId FROM dbo.Orders WHERE Quantity > 10;",
                CompliantExplanation: "Dropping the redundant looser bound leaves the actual effective condition."),
        ]);
}
