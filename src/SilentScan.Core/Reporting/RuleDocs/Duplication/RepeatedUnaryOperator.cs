using SilentScan.Core.Predicates;
using SilentScan.Core.Reporting.Sarif;

namespace SilentScan.Core.Reporting.RuleDocs.Duplication;

internal static class RepeatedUnaryOperator
{
    public static string RuleId => SarifRuleCatalog.DuplicationRuleId(DuplicationFindingKind.RepeatedUnaryOperator);

    public static RuleDocContent Content { get; } = new(
        WhyItMatters: """
            The same unary operator (NOT, unary minus, bitwise NOT) is applied twice in a row - always
            simplifiable to a single application or none. The doubled operator is either a no-op or
            equivalent to one application, and reads as a typo either way.
            """,
        HowToFixIt: "Simplify to a single application of the operator (or remove it entirely if the double application cancels out).",
        Examples:
        [
            new RuleDocExample(
                Title: "A double negation",
                NoncompliantSql: "SELECT OrderId FROM dbo.Orders WHERE NOT NOT (Status = 'Active');",
                NoncompliantExplanation: "Applying NOT twice cancels out - the condition is equivalent to Status = 'Active' with no negation at all.",
                CompliantSql: "SELECT OrderId FROM dbo.Orders WHERE Status = 'Active';",
                CompliantExplanation: "With the redundant double negation removed, the condition means the same thing and reads clearly."),
        ]);
}
