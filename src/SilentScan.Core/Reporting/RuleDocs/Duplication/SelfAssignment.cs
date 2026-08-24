using SilentScan.Core.Predicates;
using SilentScan.Core.Reporting.Sarif;

namespace SilentScan.Core.Reporting.RuleDocs.Duplication;

internal static class SelfAssignment
{
    public static string RuleId => SarifRuleCatalog.DuplicationRuleId(DuplicationFindingKind.SelfAssignment);

    public static RuleDocContent Content { get; } = new(
        WhyItMatters: """
            A pure no-op assignment: a variable or UPDATE column assigned to itself. It does nothing,
            and usually signals a copy-paste mistake where a different source column or expression was
            intended.
            """,
        HowToFixIt: "Delete the no-op self-assignment.",
        Examples:
        [
            new RuleDocExample(
                Title: "A column assigned to itself",
                NoncompliantSql: "UPDATE dbo.Orders SET Status = Status WHERE OrderId = @orderId;",
                NoncompliantExplanation: "Status is assigned to itself - the statement changes nothing and likely meant to assign a different value.",
                CompliantSql: "UPDATE dbo.Orders SET Status = @newStatus WHERE OrderId = @orderId;",
                CompliantExplanation: "The assignment now sets Status to the actually intended value."),
        ]);
}
