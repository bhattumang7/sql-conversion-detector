using SilentScan.Core.Predicates;
using SilentScan.Core.Reporting.Sarif;

namespace SilentScan.Core.Reporting.RuleDocs.DeprecatedSyntax;

internal static class LikeWithNoWildcard
{
    public static string RuleId => SarifRuleCatalog.DeprecatedSyntaxRuleId(DeprecatedSyntaxFindingKind.LikeWithNoWildcard);

    public static RuleDocContent Content { get; } = new(
        WhyItMatters: """
            A LIKE pattern contains no wildcard character - behaviorally equivalent to a plain "="
            comparison. Writing it as LIKE implies pattern matching is happening when it is not,
            misleading a reader about what the predicate actually does.
            """,
        HowToFixIt: "Use a plain = comparison instead of LIKE when the pattern has no wildcard.",
        Examples:
        [
            new RuleDocExample(
                Title: "A LIKE pattern with no wildcard character",
                NoncompliantSql: "SELECT OrderId FROM dbo.Orders WHERE Status LIKE 'Cancelled';",
                NoncompliantExplanation: "The LIKE pattern contains no wildcard, so it behaves identically to a plain equality check while implying pattern matching.",
                CompliantSql: "SELECT OrderId FROM dbo.Orders WHERE Status = 'Cancelled';",
                CompliantExplanation: "A plain = comparison expresses the same check without implying pattern matching."),
        ]);
}
