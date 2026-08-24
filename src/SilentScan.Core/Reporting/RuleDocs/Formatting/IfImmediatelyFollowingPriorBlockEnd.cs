using SilentScan.Core.Predicates;
using SilentScan.Core.Reporting.Sarif;

namespace SilentScan.Core.Reporting.RuleDocs.Formatting;

internal static class IfImmediatelyFollowingPriorBlockEnd
{
    public static string RuleId => SarifRuleCatalog.FormattingRuleId(FormattingFindingKind.IfImmediatelyFollowingPriorBlockEnd);

    public static RuleDocContent Content { get; } = new(
        WhyItMatters: """
            An IF immediately follows the closing END of a prior braced IF on the same line - easy to
            misread as an ELSE IF continuation when it is really a separate, unconditional statement.
            The statement's own behavior is unaffected - only a future edit relying on the misleading
            visual shape is at risk.
            """,
        HowToFixIt: """
            Put the following IF on its own line below the prior block's END, or use ELSE IF if it
            was actually meant to be a continuation of the same conditional chain.
            """,
        Examples:
        [
            new RuleDocExample(
                Title: "An unrelated IF sharing a line with the prior block's END",
                NoncompliantSql: """
                    IF @status = 'Active'
                    BEGIN
                        SELECT 1;
                    END IF @region = 'US'
                    BEGIN
                        SELECT 2;
                    END
                    """,
                NoncompliantExplanation: "The second IF sits on the same line as the first block's END, reading as though it continues the first conditional when it is actually independent.",
                CompliantSql: """
                    IF @status = 'Active'
                    BEGIN
                        SELECT 1;
                    END

                    IF @region = 'US'
                    BEGIN
                        SELECT 2;
                    END
                    """,
                CompliantExplanation: "Separating the two IFs onto their own lines makes clear they are independent conditionals."),
        ]);
}
