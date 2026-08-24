using SilentScan.Core.Predicates;
using SilentScan.Core.Reporting.Sarif;

namespace SilentScan.Core.Reporting.RuleDocs.Duplication;

internal static class AllBranchesIdentical
{
    public static string RuleId => SarifRuleCatalog.DuplicationRuleId(DuplicationFindingKind.AllBranchesIdentical);

    public static RuleDocContent Content { get; } = new(
        WhyItMatters: """
            Every branch of a conditional structure, including its ELSE, has an identical body or
            result - the structure produces the same outcome no matter which branch is taken. The
            whole conditional is dead weight that only obscures the one outcome that always happens.
            """,
        HowToFixIt: "Remove the conditional structure - it produces the same outcome no matter which branch is taken.",
        Examples:
        [
            new RuleDocExample(
                Title: "A CASE expression where every branch produces the same result",
                NoncompliantSql: """
                    SELECT OrderId,
                        CASE
                            WHEN Status = 'Active' THEN 'Reviewed'
                            WHEN Status = 'Cancelled' THEN 'Reviewed'
                            ELSE 'Reviewed'
                        END AS ReviewFlag
                    FROM dbo.Orders;
                    """,
                NoncompliantExplanation: "Every branch produces 'Reviewed' - the CASE always evaluates to the same value regardless of Status.",
                CompliantSql: "SELECT OrderId, 'Reviewed' AS ReviewFlag FROM dbo.Orders;",
                CompliantExplanation: "Removing the pointless CASE makes clear the value is always 'Reviewed'."),
        ]);
}
