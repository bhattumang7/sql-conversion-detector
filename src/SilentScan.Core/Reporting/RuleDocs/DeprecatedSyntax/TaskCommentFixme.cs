using SilentScan.Core.Predicates;
using SilentScan.Core.Reporting.Sarif;

namespace SilentScan.Core.Reporting.RuleDocs.DeprecatedSyntax;

internal static class TaskCommentFixme
{
    public static string RuleId => SarifRuleCatalog.DeprecatedSyntaxRuleId(DeprecatedSyntaxFindingKind.TaskCommentFixme);

    public static RuleDocContent Content { get; } = new(
        WhyItMatters: """
            A comment contains an untracked "FIXME" marker. Left in the source with no ticket or
            owner attached, it tends to sit unresolved indefinitely - the comment flags a known
            defect, but nothing tracks whether it ever gets fixed.
            """,
        HowToFixIt: """
            Either fix the issue now, or replace the marker with a reference to a tracked work item
            so it has an owner and a way to be closed out.
            """,
        Examples:
        [
            new RuleDocExample(
                Title: "An untracked FIXME marker",
                NoncompliantSql: """
                    -- FIXME: this misses orders placed on the last day of the month
                    SELECT OrderId FROM dbo.Orders WHERE OrderDate < @cutoff;
                    """,
                NoncompliantExplanation: "The FIXME flags a known defect but is not linked to any tracked work item, so it is easy to lose track of.",
                CompliantSql: """
                    -- See work item ORD-511: this misses orders placed on the last day of the month
                    SELECT OrderId FROM dbo.Orders WHERE OrderDate < @cutoff;
                    """,
                CompliantExplanation: "The comment now references a tracked work item, giving the defect an owner and a way to be closed."),
        ]);
}
