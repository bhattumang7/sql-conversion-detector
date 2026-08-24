using SilentScan.Core.Predicates;
using SilentScan.Core.Reporting.Sarif;

namespace SilentScan.Core.Reporting.RuleDocs.Formatting;

internal static class RedundantParentheses
{
    public static string RuleId => SarifRuleCatalog.FormattingRuleId(FormattingFindingKind.RedundantParentheses);

    public static RuleDocContent Content { get; } = new(
        WhyItMatters: """
            A parenthesized expression whose parentheses do not change grouping or precedence at all.
            Purely a readability signal - no query result or plan is affected. Redundant parentheses
            add visual noise a reader has to parse through to find the parentheses that actually
            matter.
            """,
        HowToFixIt: """
            Remove the parentheses; they can be deleted without changing what the expression means.
            """,
        Examples:
        [
            new RuleDocExample(
                Title: "Parentheses that add nothing to precedence or grouping",
                NoncompliantSql: "SELECT OrderId FROM dbo.Orders WHERE ((Status = 'Active'));",
                NoncompliantExplanation: "The doubled parentheses around the comparison change nothing - they can be removed without altering the expression.",
                CompliantSql: "SELECT OrderId FROM dbo.Orders WHERE Status = 'Active';",
                CompliantExplanation: "With the redundant parentheses removed, the condition reads the same and means the same thing."),
        ]);
}
