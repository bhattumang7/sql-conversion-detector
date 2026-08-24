using SilentScan.Core.Predicates;
using SilentScan.Core.Reporting.Sarif;

namespace SilentScan.Core.Reporting.RuleDocs.DeprecatedSyntax;

internal static class EqualsNullComparison
{
    public static string RuleId => SarifRuleCatalog.DeprecatedSyntaxRuleId(DeprecatedSyntaxFindingKind.EqualsNullComparison);

    public static RuleDocContent Content { get; } = new(
        WhyItMatters: """
            "= NULL" never matches any row under the default ANSI_NULLS ON session setting, including
            a genuinely NULL value - use "IS NULL" instead. A predicate written this way silently
            returns no rows for the NULL case it looks like it is meant to match.
            """,
        HowToFixIt: "Use IS NULL instead of = NULL.",
        Examples:
        [
            new RuleDocExample(
                Title: "A predicate written as = NULL",
                NoncompliantSql: "SELECT OrderId FROM dbo.Orders WHERE ShippedDate = NULL;",
                NoncompliantExplanation: "Under ANSI_NULLS ON, = NULL never matches any row, including rows where ShippedDate really is NULL - the query silently returns nothing.",
                CompliantSql: "SELECT OrderId FROM dbo.Orders WHERE ShippedDate IS NULL;",
                CompliantExplanation: "IS NULL correctly matches rows where ShippedDate is NULL."),
        ]);
}
