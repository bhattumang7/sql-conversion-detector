using SilentScan.Core.Predicates;
using SilentScan.Core.Reporting.Sarif;

namespace SilentScan.Core.Reporting.RuleDocs.CodeMetric;

internal static class ModuleTooLong
{
    public static string RuleId => SarifRuleCatalog.CodeMetricRuleId(CodeMetricFindingKind.ModuleTooLong);

    public static RuleDocContent Content { get; } = new(
        WhyItMatters: """
            A module (or, in file-mode scanning, a source file) exceeds the configured maximum line
            count. Purely a maintainability signal - no query result or execution plan is affected.
            A single procedure or script that keeps growing past this threshold usually means it is
            doing more than one job and has become hard to review as a whole, since a reviewer has
            to hold the entire thing in mind to reason about any one part of it.
            """,
        HowToFixIt: """
            Split the module along its natural seams - separate procedures for separate
            responsibilities, or separate scripts for separate deployment units - so each piece
            stays small enough to review on its own.
            """,
        Examples:
        [
            new RuleDocExample(
                Title: "A single procedure that has grown past the configured line limit",
                NoncompliantSql: "CREATE PROCEDURE dbo.DoEverything AS BEGIN /* hundreds of lines covering validation, several unrelated updates, and reporting */ END",
                NoncompliantExplanation: "One procedure has accumulated far more logic than the configured line-count threshold allows, making it hard to review as a single unit.",
                CompliantSql: "CREATE PROCEDURE dbo.ValidateOrder AS BEGIN /* ... */ END\nCREATE PROCEDURE dbo.ApplyOrderUpdates AS BEGIN /* ... */ END\nCREATE PROCEDURE dbo.ReportOrderStatus AS BEGIN /* ... */ END",
                CompliantExplanation: "Splitting the procedure along its distinct responsibilities keeps each one under the threshold and easier to review independently."),
        ]);
}
