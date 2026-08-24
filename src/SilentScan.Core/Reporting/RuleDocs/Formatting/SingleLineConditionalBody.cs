using SilentScan.Core.Predicates;
using SilentScan.Core.Reporting.Sarif;

namespace SilentScan.Core.Reporting.RuleDocs.Formatting;

internal static class SingleLineConditionalBody
{
    public static string RuleId => SarifRuleCatalog.FormattingRuleId(FormattingFindingKind.SingleLineConditionalBody);

    public static RuleDocContent Content { get; } = new(
        WhyItMatters: """
            An IF/WHILE/ELSE body is a single unbraced statement sharing its own keyword's line -
            visually easy to misread. Purely a readability signal - no query result or plan is
            affected.
            """,
        HowToFixIt: """
            Put the body on its own line below the IF/WHILE/ELSE keyword, or wrap it in BEGIN...END.
            """,
        Examples:
        [
            new RuleDocExample(
                Title: "A conditional body sharing the IF's own line",
                NoncompliantSql: "IF @status = 'Active' UPDATE dbo.Orders SET LastChecked = SYSUTCDATETIME() WHERE Status = @status;",
                NoncompliantExplanation: "The UPDATE sits on the same line as the IF, making it easy to miss that it is conditional at all.",
                CompliantSql: """
                    IF @status = 'Active'
                        UPDATE dbo.Orders SET LastChecked = SYSUTCDATETIME() WHERE Status = @status;
                    """,
                CompliantExplanation: "The body now sits on its own line, making the conditional visually obvious."),
        ]);
}
