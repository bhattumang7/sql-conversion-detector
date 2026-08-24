using SilentScan.Core.Predicates;
using SilentScan.Core.Reporting.Sarif;

namespace SilentScan.Core.Reporting.RuleDocs.CodeMetric;

internal static class RoutineTooLong
{
    public static string RuleId => SarifRuleCatalog.CodeMetricRuleId(CodeMetricFindingKind.RoutineTooLong);

    public static RuleDocContent Content { get; } = new(
        WhyItMatters: """
            A procedure, function, or trigger body exceeds the configured maximum line count.
            Purely a maintainability signal - no query result or execution plan is affected. Unlike
            ModuleTooLong, which looks at the whole file/module, this looks at a single routine body
            - a routine that keeps growing past this threshold is a strong sign it has taken on more
            than one responsibility.
            """,
        HowToFixIt: """
            Extract cohesive pieces of the routine's body into their own procedures or functions, so
            each routine stays focused and under the configured line limit.
            """,
        Examples:
        [
            new RuleDocExample(
                Title: "One trigger body handling several unrelated concerns",
                NoncompliantSql: "CREATE TRIGGER dbo.trg_Orders_AfterInsert ON dbo.Orders AFTER INSERT AS BEGIN /* hundreds of lines: audit logging, inventory adjustment, notification queueing */ END",
                NoncompliantExplanation: "The trigger body has grown past the configured line-count threshold by combining several unrelated pieces of logic into one routine.",
                CompliantSql: "CREATE TRIGGER dbo.trg_Orders_AfterInsert ON dbo.Orders AFTER INSERT AS BEGIN EXEC dbo.LogOrderAudit; EXEC dbo.AdjustInventory; EXEC dbo.QueueOrderNotification; END",
                CompliantExplanation: "Extracting each concern into its own procedure keeps the trigger body short and each extracted piece independently reviewable."),
        ]);
}
