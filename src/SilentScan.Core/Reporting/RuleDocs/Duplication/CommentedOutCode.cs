using SilentScan.Core.Predicates;
using SilentScan.Core.Reporting.Sarif;

namespace SilentScan.Core.Reporting.RuleDocs.Duplication;

internal static class CommentedOutCode
{
    public static string RuleId => SarifRuleCatalog.DuplicationRuleId(DuplicationFindingKind.CommentedOutCode);

    public static RuleDocContent Content { get; } = new(
        WhyItMatters: """
            A comment's own stripped content reparses cleanly as a plausible T-SQL statement or batch
            - not prose that merely mentions SQL keywords. Dead code left in a comment accumulates
            with no indication of whether it is safe to delete or was left for a reason.
            """,
        HowToFixIt: "Delete the commented-out code - version control already preserves its history.",
        Examples:
        [
            new RuleDocExample(
                Title: "A commented-out statement",
                NoncompliantSql: """
                    -- SELECT OrderId FROM dbo.Orders WHERE Status = 'Cancelled';
                    SELECT OrderId FROM dbo.Orders WHERE Status = 'Active';
                    """,
                NoncompliantExplanation: "The comment's content reparses as a full valid statement - dead code left behind rather than prose.",
                CompliantSql: "SELECT OrderId FROM dbo.Orders WHERE Status = 'Active';",
                CompliantExplanation: "The dead statement is removed; version control still has it if it is ever needed again."),
        ]);
}
