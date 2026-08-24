using SilentScan.Core.Predicates;
using SilentScan.Core.Reporting.Sarif;

namespace SilentScan.Core.Reporting.RuleDocs.Formatting;

internal static class DanglingStatementAfterUnbracedBody
{
    public static string RuleId => SarifRuleCatalog.FormattingRuleId(FormattingFindingKind.DanglingStatementAfterUnbracedBody);

    public static RuleDocContent Content { get; } = new(
        WhyItMatters: """
            A statement immediately follows an unbraced IF/WHILE's single-statement body, visually
            appearing to still be inside the conditional/loop when it is not. The statement's own
            behavior is unaffected - only a future edit relying on the misleading visual shape is at
            risk.
            """,
        HowToFixIt: """
            Wrap the conditional/loop body in BEGIN...END so its extent is unambiguous, or add a
            blank line before the following statement to separate it visually.
            """,
        Examples:
        [
            new RuleDocExample(
                Title: "A statement that looks like part of the IF above it",
                NoncompliantSql: """
                    IF @status = 'Active'
                        UPDATE dbo.Orders SET LastChecked = SYSUTCDATETIME() WHERE Status = @status;
                    DELETE FROM dbo.OrderStaging WHERE Status = @status;
                    """,
                NoncompliantExplanation: "The DELETE sits directly below the unbraced IF body at the same indentation, visually reading as part of the conditional even though it always runs.",
                CompliantSql: """
                    IF @status = 'Active'
                    BEGIN
                        UPDATE dbo.Orders SET LastChecked = SYSUTCDATETIME() WHERE Status = @status;
                    END
                    DELETE FROM dbo.OrderStaging WHERE Status = @status;
                    """,
                CompliantExplanation: "BEGIN...END makes the IF's extent explicit, so the DELETE is unambiguously outside it."),
        ]);
}
