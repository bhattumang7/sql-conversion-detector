using SilentScan.Core.Predicates;
using SilentScan.Core.Reporting.Sarif;

namespace SilentScan.Core.Reporting.RuleDocs.WindowFunctionArgument;

internal static class PercentileOutOfRange
{
    public static string RuleId => SarifRuleCatalog.WindowFunctionArgumentRuleId(WindowFunctionArgumentFindingKind.PercentileOutOfRange);

    public static RuleDocContent Content { get; } = new(
        WhyItMatters: """
            PERCENTILE_CONT/PERCENTILE_DISC's percentile argument names a fraction of the ordered
            set, and only the inclusive range [0, 1] is a valid fraction - 0 selects the minimum,
            1 selects the maximum, and both ends were probed directly against a real engine and
            confirmed valid. A percentile argument that constant-folds to a value outside that
            range - whether written as a bare literal or as an arithmetic expression that folds to
            one - raises Msg 8727 ("Input parameter of percentile function is outside of range
            [0, 1]") the moment any row actually reaches the function. A statement whose FROM
            clause matches zero rows never reaches the check and never raises, but any statement
            that does match a row fails outright, every time it runs.

            This is pure source-level constant-folding with no catalog dependency - the same shape
            as the LAG/LEAD offset argument being a compile-time constant regardless of whether it
            is a literal or an expression that folds to one.
            """,
        HowToFixIt: """
            Change the percentile argument to a value between 0 and 1 inclusive.
            """,
        Examples:
        [
            new RuleDocExample(
                Title: "A PERCENTILE_CONT call with an out-of-range percentile",
                NoncompliantSql: "SELECT PERCENTILE_CONT(1.5) WITHIN GROUP (ORDER BY Amt) OVER () FROM dbo.Sales;",
                NoncompliantExplanation: "The percentile argument 1.5 constant-folds to a value outside [0, 1] - the statement raises Msg 8727 the moment any row reaches the function.",
                CompliantSql: "SELECT PERCENTILE_CONT(0.5) WITHIN GROUP (ORDER BY Amt) OVER () FROM dbo.Sales;",
                CompliantExplanation: "0.5 is inside the inclusive [0, 1] range and selects the median."),
        ]);
}
