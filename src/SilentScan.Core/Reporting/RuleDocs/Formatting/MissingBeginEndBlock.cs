using SilentScan.Core.Predicates;
using SilentScan.Core.Reporting.Sarif;

namespace SilentScan.Core.Reporting.RuleDocs.Formatting;

internal static class MissingBeginEndBlock
{
    public static string RuleId => SarifRuleCatalog.FormattingRuleId(FormattingFindingKind.MissingBeginEndBlock);

    public static RuleDocContent Content { get; } = new(
        WhyItMatters: """
            An IF/WHILE/ELSE body is a single statement with no BEGIN...END - a later statement added
            here without braces silently falls outside the conditional. Purely a maintainability risk
            - no query result or plan is affected as the code stands, but the very next edit is one
            indentation mistake away from changing behavior silently.
            """,
        HowToFixIt: """
            Wrap the body in BEGIN...END even though it is currently a single statement, so a future
            statement added inside it stays inside the conditional.
            """,
        Examples:
        [
            new RuleDocExample(
                Title: "An unbraced IF body",
                NoncompliantSql: """
                    IF @status = 'Active'
                        UPDATE dbo.Orders SET LastChecked = SYSUTCDATETIME() WHERE Status = @status;
                    """,
                NoncompliantExplanation: "The IF body has no BEGIN...END - a statement added below the UPDATE, indented to look like part of the IF, would actually run unconditionally.",
                CompliantSql: """
                    IF @status = 'Active'
                    BEGIN
                        UPDATE dbo.Orders SET LastChecked = SYSUTCDATETIME() WHERE Status = @status;
                    END
                    """,
                CompliantExplanation: "BEGIN...END makes the conditional's extent explicit, so a statement added inside it stays inside it."),
        ]);
}
