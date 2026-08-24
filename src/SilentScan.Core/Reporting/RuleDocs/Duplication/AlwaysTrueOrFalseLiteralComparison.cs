using SilentScan.Core.Predicates;
using SilentScan.Core.Reporting.Sarif;

namespace SilentScan.Core.Reporting.RuleDocs.Duplication;

internal static class AlwaysTrueOrFalseLiteralComparison
{
    public static string RuleId => SarifRuleCatalog.DuplicationRuleId(DuplicationFindingKind.AlwaysTrueOrFalseLiteralComparison);

    public static RuleDocContent Content { get; } = new(
        WhyItMatters: """
            A comparison between two literal values (never a column or variable) is provable at parse
            time regardless of any row's real data - the predicate is dead weight or can never match.
            Either the predicate can be deleted, or it signals a literal that was meant to be a column
            or variable reference.
            """,
        Examples:
        [
            new RuleDocExample(
                Title: "A comparison between two fixed literals",
                NoncompliantSql: "SELECT OrderId FROM dbo.Orders WHERE 1 = 1 AND Status = 'Active';",
                NoncompliantExplanation: "1 = 1 compares two literals and is always true regardless of any row's data - it adds nothing to the filter.",
                CompliantSql: "SELECT OrderId FROM dbo.Orders WHERE Status = 'Active';",
                CompliantExplanation: "Removing the always-true literal comparison leaves the actual filter condition."),
        ]);
}
