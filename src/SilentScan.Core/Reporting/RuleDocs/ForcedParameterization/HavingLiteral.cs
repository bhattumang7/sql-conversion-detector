using SilentScan.Core.Predicates;
using SilentScan.Core.Reporting.Sarif;

namespace SilentScan.Core.Reporting.RuleDocs.ForcedParameterization;

internal static class HavingLiteral
{
    public static string RuleId => SarifRuleCatalog.ForcedParameterizationRuleId(ForcedParameterizationFindingKind.HavingLiteral);

    public static RuleDocContent Content { get; } = new(
        WhyItMatters: """
            Reported only when the target database has `PARAMETERIZATION FORCED` on. Confirmed
            directly against a real engine: `... GROUP BY Id HAVING COUNT(*) > 2` keeps the `2`
            literal untouched in the cached plan while the statement's own WHERE-clause equality
            correctly parameterizes.

            A reporting/aggregation query whose HAVING threshold varies (a minimum count, a sum
            cutoff) gets a fresh compile per distinct threshold under PARAMETERIZATION FORCED,
            even though the rest of the query is identical between calls.
            """,
        HowToFixIt: """
            Pass the HAVING comparand as a parameter or local variable instead of a literal.
            """,
        Examples:
        [
            new RuleDocExample(
                Title: "A literal threshold in HAVING",
                NoncompliantSql: """
                    SELECT CustomerId, COUNT(*) AS OrderCount FROM dbo.Orders GROUP BY CustomerId HAVING COUNT(*) > 5;
                    """,
                NoncompliantExplanation: "Under PARAMETERIZATION FORCED, 5 stays literal in the cached plan - a different threshold recompiles instead of reusing this plan.",
                CompliantSql: """
                    CREATE PROCEDURE dbo.GetFrequentCustomers @MinOrders int AS
                    SELECT CustomerId, COUNT(*) AS OrderCount FROM dbo.Orders GROUP BY CustomerId HAVING COUNT(*) > @MinOrders;
                    """,
                CompliantExplanation: "The threshold is already a parameter, so every call shares the one compiled plan regardless of PARAMETERIZATION FORCED."),
        ]);
}
