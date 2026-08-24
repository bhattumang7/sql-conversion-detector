using SilentScan.Core.Predicates;
using SilentScan.Core.Reporting.Sarif;

namespace SilentScan.Core.Reporting.RuleDocs.DeprecatedSyntax;

internal static class NumberedProcedureExecution
{
    public static string RuleId => SarifRuleCatalog.DeprecatedSyntaxRuleId(DeprecatedSyntaxFindingKind.NumberedProcedureExecution);

    public static RuleDocContent Content { get; } = new(
        WhyItMatters: """
            A procedure is invoked by its numbered-procedure-group number. The bare number gives a
            reader no way to tell which procedure is actually being called without cross-referencing
            the group's definition.
            """,
        HowToFixIt: "Invoke the procedure by its real name instead of its numbered-group number.",
        Examples:
        [
            new RuleDocExample(
                Title: "A procedure invoked by its group number",
                NoncompliantSql: "EXEC dbo.GetOrders;1;",
                NoncompliantExplanation: "The ;1 suffix invokes a specific member of a numbered procedure group, and gives no readable indication of which member that is.",
                CompliantSql: "EXEC dbo.GetOrders;",
                CompliantExplanation: "Invoking the procedure by name makes clear exactly what is being called."),
        ]);
}
