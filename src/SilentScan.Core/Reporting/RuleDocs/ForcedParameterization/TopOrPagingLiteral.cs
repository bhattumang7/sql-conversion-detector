using SilentScan.Core.Predicates;
using SilentScan.Core.Reporting.Sarif;

namespace SilentScan.Core.Reporting.RuleDocs.ForcedParameterization;

internal static class TopOrPagingLiteral
{
    public static string RuleId => SarifRuleCatalog.ForcedParameterizationRuleId(ForcedParameterizationFindingKind.TopOrPagingLiteral);

    public static RuleDocContent Content { get; } = new(
        WhyItMatters: """
            Reported only when the target database has `PARAMETERIZATION FORCED` on. Confirmed
            directly against a real engine: a literal `TOP N`, and a literal `OFFSET n ROWS FETCH
            NEXT m ROWS ONLY`, both stay untouched in the cached plan while an unrelated equality
            predicate in the same statement correctly parameterizes.

            A paginated query - one of the most common shapes in real application code - varying
            only its page size or offset gets a fresh compile per distinct value, right where
            PARAMETERIZATION FORCED was expected to collapse them into one shared plan.
            """,
        HowToFixIt: """
            Pass the row count as a parameter or local variable (`TOP (@N)`, `OFFSET @Skip ROWS
            FETCH NEXT @Take ROWS ONLY`) instead of a literal.
            """,
        Examples:
        [
            new RuleDocExample(
                Title: "A literal page size",
                NoncompliantSql: """
                    SELECT OrderId FROM dbo.Orders ORDER BY OrderId OFFSET 20 ROWS FETCH NEXT 10 ROWS ONLY;
                    """,
                NoncompliantExplanation: "Under PARAMETERIZATION FORCED, 20 and 10 both stay literal in the cached plan - a different page size recompiles instead of reusing this plan.",
                CompliantSql: """
                    CREATE PROCEDURE dbo.GetOrdersPage @Skip int, @Take int AS
                    SELECT OrderId FROM dbo.Orders ORDER BY OrderId OFFSET @Skip ROWS FETCH NEXT @Take ROWS ONLY;
                    """,
                CompliantExplanation: "The paging counts are already parameters, so every call shares the one compiled plan regardless of PARAMETERIZATION FORCED."),
        ]);
}
