using SilentScan.Core.Predicates;
using SilentScan.Core.Reporting.Sarif;

namespace SilentScan.Core.Reporting.RuleDocs.ForcedParameterization;

internal static class ConvertStyleCodeLiteral
{
    public static string RuleId => SarifRuleCatalog.ForcedParameterizationRuleId(ForcedParameterizationFindingKind.ConvertStyleCodeLiteral);

    public static RuleDocContent Content { get; } = new(
        WhyItMatters: """
            Reported only when the target database has `PARAMETERIZATION FORCED` on. Confirmed
            directly against a real engine: `CONVERT(varchar, GETDATE(), 101)` keeps the `101`
            style code untouched in the cached plan.

            A style code that varies by call - formatting dates differently for different report
            outputs from the same query shape, for example - gets a fresh compile per distinct
            style under PARAMETERIZATION FORCED.
            """,
        HowToFixIt: """
            Pass the CONVERT style code as a parameter or local variable instead of a literal -
            confirmed directly that the engine accepts a variable there.
            """,
        Examples:
        [
            new RuleDocExample(
                Title: "A literal CONVERT style code",
                NoncompliantSql: """
                    SELECT CONVERT(varchar, OrderDate, 101) FROM dbo.Orders;
                    """,
                NoncompliantExplanation: "Under PARAMETERIZATION FORCED, 101 stays literal in the cached plan - a different style code recompiles instead of reusing this plan.",
                CompliantSql: """
                    CREATE PROCEDURE dbo.GetOrdersFormatted @DateStyle int AS
                    SELECT CONVERT(varchar, OrderDate, @DateStyle) FROM dbo.Orders;
                    """,
                CompliantExplanation: "The style code is already a parameter, so every call shares the one compiled plan regardless of PARAMETERIZATION FORCED."),
        ]);
}
