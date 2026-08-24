using SilentScan.Core.Predicates;
using SilentScan.Core.Reporting.Sarif;

namespace SilentScan.Core.Reporting.RuleDocs.Duplication;

internal static class IdenticalBinaryOperands
{
    public static string RuleId => SarifRuleCatalog.DuplicationRuleId(DuplicationFindingKind.IdenticalBinaryOperands);

    public static RuleDocContent Content { get; } = new(
        WhyItMatters: """
            The identical expression appears on both sides of a comparison, AND/OR, or a
            self-referential arithmetic operator - always the same value, a tautology, or a fixed
            degenerate result. Writing both sides the same way almost always means one side meant to
            reference something else.
            """,
        Examples:
        [
            new RuleDocExample(
                Title: "The same expression on both sides of a comparison",
                NoncompliantSql: "SELECT OrderId FROM dbo.Orders WHERE UnitPrice > UnitPrice;",
                NoncompliantExplanation: "UnitPrice compared to itself is always false - one side likely meant to reference a different column.",
                CompliantSql: "SELECT OrderId FROM dbo.Orders WHERE UnitPrice > DiscountedPrice;",
                CompliantExplanation: "Comparing UnitPrice against a different column expresses a real, non-degenerate condition."),
        ]);
}
