using SilentScan.Core.Predicates;
using SilentScan.Core.Reporting.Sarif;

namespace SilentScan.Core.Reporting.RuleDocs.Duplication;

internal static class MutuallyExclusiveAndCondition
{
    public static string RuleId => SarifRuleCatalog.DuplicationRuleId(DuplicationFindingKind.MutuallyExclusiveAndCondition);

    public static RuleDocContent Content { get; } = new(
        WhyItMatters: """
            Two conjuncts of one AND-combined condition compare the same operand against numeric
            bounds whose ranges cannot both hold at once - the condition can never be true. Any query
            or branch guarded by this condition silently never matches anything.
            """,
        Examples:
        [
            new RuleDocExample(
                Title: "Two AND-combined bounds that can never both hold",
                NoncompliantSql: "SELECT OrderId FROM dbo.Orders WHERE Quantity > 100 AND Quantity < 10;",
                NoncompliantExplanation: "No value can be both greater than 100 and less than 10 at once - this condition can never be true, and the query always returns nothing.",
                CompliantSql: "SELECT OrderId FROM dbo.Orders WHERE Quantity > 100 OR Quantity < 10;",
                CompliantExplanation: "OR expresses the (presumably intended) union of the two ranges instead of their impossible intersection."),
        ]);
}
