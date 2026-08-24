using SilentScan.Core.Predicates;
using SilentScan.Core.Reporting.Sarif;

namespace SilentScan.Core.Reporting.RuleDocs.DeprecatedSyntax;

internal static class NotEqualsNullComparison
{
    public static string RuleId => SarifRuleCatalog.DeprecatedSyntaxRuleId(DeprecatedSyntaxFindingKind.NotEqualsNullComparison);

    public static RuleDocContent Content { get; } = new(
        WhyItMatters: """
            "<> NULL"/"!= NULL" never matches any row under the default ANSI_NULLS ON session setting
            - use "IS NOT NULL" instead. A predicate written this way silently returns no rows at all,
            not even the non-NULL rows it looks like it is meant to match.
            """,
        HowToFixIt: "Use IS NOT NULL instead of <> NULL / != NULL.",
        Examples:
        [
            new RuleDocExample(
                Title: "A predicate written as <> NULL",
                NoncompliantSql: "SELECT OrderId FROM dbo.Orders WHERE ShippedDate <> NULL;",
                NoncompliantExplanation: "Under ANSI_NULLS ON, <> NULL never matches any row - the query silently returns nothing, even for rows where ShippedDate is not NULL.",
                CompliantSql: "SELECT OrderId FROM dbo.Orders WHERE ShippedDate IS NOT NULL;",
                CompliantExplanation: "IS NOT NULL correctly matches rows where ShippedDate is not NULL."),
        ]);
}
