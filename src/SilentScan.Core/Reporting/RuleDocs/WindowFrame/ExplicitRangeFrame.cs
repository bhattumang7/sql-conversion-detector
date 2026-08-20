using SilentScan.Core.Predicates;
using SilentScan.Core.Reporting.Sarif;

namespace SilentScan.Core.Reporting.RuleDocs.WindowFrame;

internal static class ExplicitRangeFrame
{
    public static string RuleId => SarifRuleCatalog.WindowFrameRuleId(WindowFrameFindingKind.ExplicitRangeFrame);

    public static RuleDocContent Content { get; } = new(
        WhyItMatters: """
            A window function's frame clause - `ROWS BETWEEN ...` or `RANGE BETWEEN ...` - decides
            which rows around the current one participate in the aggregate, and the two keywords are
            not interchangeable in mechanism even when they happen to produce the same result set.
            `ROWS` counts physical row offsets; `RANGE` instead compares the ORDER BY key's actual
            VALUE to decide peer-group membership, which is real extra work at execution time. This
            was probed directly against a real engine: an equivalent `ROWS` frame and a `RANGE`
            frame both compile to the identical `Window Spool` physical operator (there is no
            on-disk-vs-not distinction between the two, contrary to older folklore), but that
            operator's own measured CPU cost runs roughly 4x higher under `RANGE` than the
            equivalent `ROWS` frame across repeated runs against identical data - a real, if
            modest-scale, execution-time cost from the peer-group value comparison `RANGE` requires
            that `ROWS`'s pure physical-offset counting does not.

            This is purely a performance-cost finding, not a correctness one - for the common case
            where no ties exist on the ORDER BY key, `ROWS` and `RANGE` produce the exact same
            result set, just at different cost. Version-insensitive: this is a long-standing
            execution-engine property, unaffected by compatibility level or cardinality-estimator
            mode.
            """,
        HowToFixIt: """
            Use an explicit ROWS frame instead of RANGE where peer-group semantics aren't actually
            needed (i.e. the ORDER BY key has no meaningful ties, or ties should each be treated as
            their own row rather than grouped together) - it costs materially less CPU at the Window
            Spool operator for the same result.
            """,
        Examples:
        [
            new RuleDocExample(
                Title: "An explicit RANGE frame with no tie-grouping requirement",
                NoncompliantSql: "SELECT SUM(Amt) OVER (PARTITION BY GroupId ORDER BY D RANGE BETWEEN UNBOUNDED PRECEDING AND CURRENT ROW) FROM dbo.Sales;",
                NoncompliantExplanation: "RANGE compares D's own value to decide which rows are peers of the current row - measurably more CPU at the Window Spool operator than ROWS, for a running total that doesn't need value-based peer grouping.",
                CompliantSql: "SELECT SUM(Amt) OVER (PARTITION BY GroupId ORDER BY D ROWS BETWEEN UNBOUNDED PRECEDING AND CURRENT ROW) FROM dbo.Sales;",
                CompliantExplanation: "ROWS counts physical row offsets instead of comparing D's value - the same running total, measurably less CPU."),
        ]);
}
