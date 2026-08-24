using SilentScan.Core.Predicates;
using SilentScan.Core.Reporting.Sarif;

namespace SilentScan.Core.Reporting.RuleDocs.Duplication;

internal static class NegatedComparisonAsOpposite
{
    public static string RuleId => SarifRuleCatalog.DuplicationRuleId(DuplicationFindingKind.NegatedComparisonAsOpposite);

    public static RuleDocContent Content { get; } = new(
        WhyItMatters: """
            A negated comparison is written instead of its provably equivalent opposite operator - a
            readability suggestion, not a correctness claim. NOT (a > b) reads slower than a <= b for
            the exact same result.
            """,
        HowToFixIt: "Rewrite the negated comparison using its provably equivalent opposite operator.",
        Examples:
        [
            new RuleDocExample(
                Title: "A negated comparison instead of its opposite operator",
                NoncompliantSql: "SELECT OrderId FROM dbo.Orders WHERE NOT (Quantity > 0);",
                NoncompliantExplanation: "NOT (Quantity > 0) is provably equivalent to Quantity <= 0 but takes longer to read.",
                CompliantSql: "SELECT OrderId FROM dbo.Orders WHERE Quantity <= 0;",
                CompliantExplanation: "The opposite operator expresses the identical condition without the negation."),
        ]);
}
