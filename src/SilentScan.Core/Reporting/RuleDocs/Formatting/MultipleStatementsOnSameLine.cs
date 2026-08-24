using SilentScan.Core.Predicates;
using SilentScan.Core.Reporting.Sarif;

namespace SilentScan.Core.Reporting.RuleDocs.Formatting;

internal static class MultipleStatementsOnSameLine
{
    public static string RuleId => SarifRuleCatalog.FormattingRuleId(FormattingFindingKind.MultipleStatementsOnSameLine);

    public static RuleDocContent Content { get; } = new(
        WhyItMatters: """
            Two or more statements in the same block start on the same physical source line. Purely
            a readability signal - no query result or plan is affected. A diff or code review that
            highlights whole lines can hide the fact that two separate statements changed, since both
            sit on the one line that was touched.
            """,
        Examples:
        [
            new RuleDocExample(
                Title: "Two statements sharing one source line",
                NoncompliantSql: "SET @count = 0; SET @total = 0;",
                NoncompliantExplanation: "Both assignments start on the same physical line, so a line-based diff or review tool shows them as a single changed line.",
                CompliantSql: "SET @count = 0;\nSET @total = 0;",
                CompliantExplanation: "Each statement now occupies its own line, so a diff shows exactly which one changed."),
        ]);
}
