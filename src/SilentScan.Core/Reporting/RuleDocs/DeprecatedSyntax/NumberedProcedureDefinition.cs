using SilentScan.Core.Predicates;
using SilentScan.Core.Reporting.Sarif;

namespace SilentScan.Core.Reporting.RuleDocs.DeprecatedSyntax;

internal static class NumberedProcedureDefinition
{
    public static string RuleId => SarifRuleCatalog.DeprecatedSyntaxRuleId(DeprecatedSyntaxFindingKind.NumberedProcedureDefinition);

    public static RuleDocContent Content { get; } = new(
        WhyItMatters: """
            A procedure is defined as a numbered-procedure-group member - a deprecated T-SQL feature
            still accepted by the parser and engine. Numbered groups share a single set of
            permissions and cannot be scripted or dropped individually, which surprises anyone
            expecting ordinary named procedures.
            """,
        HowToFixIt: "Define separate, individually named procedures instead of a numbered-procedure-group member.",
        Examples:
        [
            new RuleDocExample(
                Title: "A procedure defined as a numbered-group member",
                NoncompliantSql: """
                    CREATE PROCEDURE dbo.GetOrders;1
                    AS
                    BEGIN
                        SELECT OrderId FROM dbo.Orders;
                    END
                    """,
                NoncompliantExplanation: "The ;1 suffix makes this a member of a numbered procedure group, a deprecated feature with permissions and drop semantics shared across the whole group.",
                CompliantSql: """
                    CREATE PROCEDURE dbo.GetOrders
                    AS
                    BEGIN
                        SELECT OrderId FROM dbo.Orders;
                    END
                    """,
                CompliantExplanation: "Without the numbered-group suffix, the procedure is an ordinary, independently manageable object."),
        ]);
}
