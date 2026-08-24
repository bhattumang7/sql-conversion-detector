using SilentScan.Core.Predicates;
using SilentScan.Core.Reporting.Sarif;

namespace SilentScan.Core.Reporting.RuleDocs.DeprecatedSyntax;

internal static class TaskCommentTodo
{
    public static string RuleId => SarifRuleCatalog.DeprecatedSyntaxRuleId(DeprecatedSyntaxFindingKind.TaskCommentTodo);

    public static RuleDocContent Content { get; } = new(
        WhyItMatters: """
            A comment contains an untracked "TODO" marker. Left in the source with no ticket or
            owner attached, it tends to sit unresolved indefinitely - the comment records that
            something was left unfinished, but nothing tracks whether it ever gets done.
            """,
        HowToFixIt: """
            Either resolve the TODO now, or replace it with a reference to a tracked work item so it
            has an owner and a way to be closed out.
            """,
        Examples:
        [
            new RuleDocExample(
                Title: "An untracked TODO marker",
                NoncompliantSql: """
                    -- TODO: handle the cancelled-order case
                    SELECT OrderId FROM dbo.Orders WHERE Status = 'Active';
                    """,
                NoncompliantExplanation: "The TODO names a gap in the logic but is not linked to any tracked work item, so it is easy to lose track of.",
                CompliantSql: """
                    -- See work item ORD-482: handle the cancelled-order case
                    SELECT OrderId FROM dbo.Orders WHERE Status = 'Active';
                    """,
                CompliantExplanation: "The comment now references a tracked work item, giving the gap an owner and a way to be closed."),
        ]);
}
