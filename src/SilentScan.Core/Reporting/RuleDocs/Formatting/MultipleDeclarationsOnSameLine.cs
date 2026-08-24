using SilentScan.Core.Predicates;
using SilentScan.Core.Reporting.Sarif;

namespace SilentScan.Core.Reporting.RuleDocs.Formatting;

internal static class MultipleDeclarationsOnSameLine
{
    public static string RuleId => SarifRuleCatalog.FormattingRuleId(FormattingFindingKind.MultipleDeclarationsOnSameLine);

    public static RuleDocContent Content { get; } = new(
        WhyItMatters: """
            Two or more variables in the same DECLARE are declared on the same physical source line.
            Purely a readability signal - no query result or plan is affected. A diff or code review
            that highlights whole lines can hide the fact that two separate declarations changed,
            since both sit on the one line that was touched.
            """,
        Examples:
        [
            new RuleDocExample(
                Title: "Two variables declared on one line",
                NoncompliantSql: "DECLARE @orderId INT, @customerId INT;",
                NoncompliantExplanation: "Both declarations sit on the same physical line, so a line-based diff shows them as a single changed line.",
                CompliantSql: "DECLARE @orderId INT;\nDECLARE @customerId INT;",
                CompliantExplanation: "Each declaration now occupies its own line, so a diff shows exactly which one changed."),
        ]);
}
