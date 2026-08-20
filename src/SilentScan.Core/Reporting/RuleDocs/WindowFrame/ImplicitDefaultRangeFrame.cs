using SilentScan.Core.Predicates;
using SilentScan.Core.Reporting.Sarif;

namespace SilentScan.Core.Reporting.RuleDocs.WindowFrame;

internal static class ImplicitDefaultRangeFrame
{
    public static string RuleId => SarifRuleCatalog.WindowFrameRuleId(WindowFrameFindingKind.ImplicitDefaultRangeFrame);

    public static RuleDocContent Content { get; } = new(
        WhyItMatters: """
            A window function's `OVER` clause can carry an `ORDER BY` with no frame clause at all -
            no `ROWS`/`RANGE` keyword anywhere in the source text. T-SQL doesn't leave this
            undefined: it silently defaults to `RANGE BETWEEN UNBOUNDED PRECEDING AND CURRENT ROW`,
            the exact same mechanism (and, oracle-confirmed, the exact same measured cost) as writing
            that `RANGE` frame explicitly. The difference from this tool's separate explicit-RANGE
            finding is entirely about visibility: here, nothing in the query text says RANGE at all -
            a reader has to already know T-SQL's own default-frame rule to realize the more
            expensive path is the one actually running.

            This makes the implicit case arguably the more important one to catch: an author who
            never intended a RANGE frame, and never typed the word RANGE, still pays its cost simply
            by omitting a frame clause after an ORDER BY - the exact opposite of what the source text
            suggests happened.
            """,
        HowToFixIt: """
            Add an explicit ROWS frame instead of relying on the implicit default RANGE BETWEEN
            UNBOUNDED PRECEDING AND CURRENT ROW - this both documents the intended frame in the
            source text and avoids the RANGE frame's measured extra CPU cost.
            """,
        Examples:
        [
            new RuleDocExample(
                Title: "An ORDER BY with no frame clause, defaulting to RANGE invisibly",
                NoncompliantSql: "SELECT SUM(Amt) OVER (PARTITION BY GroupId ORDER BY D) FROM dbo.Sales;",
                NoncompliantExplanation: "Nothing in this text says RANGE, but T-SQL's own default rule makes this an implicit RANGE BETWEEN UNBOUNDED PRECEDING AND CURRENT ROW frame - the same measured cost as writing RANGE explicitly, invisible to a reader who doesn't already know the default.",
                CompliantSql: "SELECT SUM(Amt) OVER (PARTITION BY GroupId ORDER BY D ROWS BETWEEN UNBOUNDED PRECEDING AND CURRENT ROW) FROM dbo.Sales;",
                CompliantExplanation: "The frame is now explicit ROWS, both documenting the intended behavior in the source text and avoiding RANGE's extra cost."),
        ]);
}
