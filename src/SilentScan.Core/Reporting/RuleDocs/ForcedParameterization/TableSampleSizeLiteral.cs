using SilentScan.Core.Predicates;
using SilentScan.Core.Reporting.Sarif;

namespace SilentScan.Core.Reporting.RuleDocs.ForcedParameterization;

internal static class TableSampleSizeLiteral
{
    public static string RuleId => SarifRuleCatalog.ForcedParameterizationRuleId(ForcedParameterizationFindingKind.TableSampleSizeLiteral);

    public static RuleDocContent Content { get; } = new(
        WhyItMatters: """
            Reported only when the target database has `PARAMETERIZATION FORCED` on. Confirmed
            directly against a real engine: `TABLESAMPLE (10 PERCENT)` keeps the `10` literal
            untouched in the cached plan while an unrelated equality predicate in the same
            statement correctly parameterizes.

            A sampling percentage or row count that varies by call - a data-profiling tool that
            samples more aggressively on demand, for example - gets a fresh compile per distinct
            sample size under PARAMETERIZATION FORCED.

            Unlike every other member of this rule family, there is no parameter workaround here:
            `TABLESAMPLE` and `REPEATABLE` clauses reject a variable outright (confirmed directly:
            Msg 497, "Variables are not allowed in the TABLESAMPLE or REPEATABLE clauses") - a
            varying sample size is unavoidably a fresh compile under PARAMETERIZATION FORCED, not
            a fixable oversight.
            """,
        HowToFixIt: """
            No parameter workaround exists - TABLESAMPLE rejects a variable outright. Keep the
            sample size fixed per query text if the recompile cost matters, or accept it.
            """,
        Examples:
        [
            new RuleDocExample(
                Title: "A literal TABLESAMPLE size",
                NoncompliantSql: """
                    SELECT OrderId FROM dbo.Orders TABLESAMPLE (10 PERCENT);
                    """,
                NoncompliantExplanation: "Under PARAMETERIZATION FORCED, 10 stays literal in the cached plan - a different sample size recompiles instead of reusing this plan. TABLESAMPLE cannot take a variable at all (Msg 497), so this cannot be fixed by parameterizing it."),
        ]);
}
