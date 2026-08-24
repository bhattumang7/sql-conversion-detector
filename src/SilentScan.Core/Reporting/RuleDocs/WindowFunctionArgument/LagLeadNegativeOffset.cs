using SilentScan.Core.Predicates;
using SilentScan.Core.Reporting.Sarif;

namespace SilentScan.Core.Reporting.RuleDocs.WindowFunctionArgument;

internal static class LagLeadNegativeOffset
{
    public static string RuleId => SarifRuleCatalog.WindowFunctionArgumentRuleId(WindowFunctionArgumentFindingKind.LagLeadNegativeOffset);

    public static RuleDocContent Content { get; } = new(
        WhyItMatters: """
            LAG/LEAD's offset argument tells the engine how many rows back or forward, within the
            current partition, to read the value from. This was probed directly against a real
            engine: an offset that constant-folds to a negative value - whether written as a bare
            negative literal or as an arithmetic expression that folds to one - raises Msg 8730
            ("Offset parameter for Lag and Lead functions cannot be a negative value") the moment
            any row actually reaches the window function. A statement whose FROM clause matches
            zero rows never reaches the check and never raises, but any statement that does match a
            row fails outright, every time it runs.

            This is pure source-level constant-folding with no catalog dependency - the same shape
            as the offset/percentile argument being a compile-time constant regardless of whether
            it is a literal or an expression that folds to one.
            """,
        HowToFixIt: """
            Change the offset argument to a non-negative value - LAG/LEAD's offset counts rows
            away from the current row and has no meaning as a negative number.
            """,
        Examples:
        [
            new RuleDocExample(
                Title: "A LAG call with a negative offset",
                NoncompliantSql: "SELECT LAG(Amt, -1) OVER (ORDER BY D) FROM dbo.Sales;",
                NoncompliantExplanation: "The offset argument -1 constant-folds to a negative value - the statement raises Msg 8730 the moment any row reaches the window function.",
                CompliantSql: "SELECT LAG(Amt, 1) OVER (ORDER BY D) FROM dbo.Sales;",
                CompliantExplanation: "A non-negative offset is valid - LEAD(Amt, 1) would read one row forward instead of back."),
        ]);
}
