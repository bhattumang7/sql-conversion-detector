using SilentScan.Core.Predicates;
using SilentScan.Core.Reporting.Sarif;

namespace SilentScan.Core.Reporting.RuleDocs.View;

internal static class TopPercentOrderByNeverLimits
{
    public static string RuleId => SarifRuleCatalog.ViewOrderingRuleId(ViewOrderingFindingKind.TopPercentOrderByNeverLimits);

    public static RuleDocContent Content { get; } = new(
        WhyItMatters: """
            T-SQL forbids a bare `ORDER BY` inside a view or inline TVF's own outermost query (Msg
            1033) unless it's paired with `TOP`, `OFFSET...FETCH`, or `FOR XML` - so a common
            workaround is `TOP (100) PERCENT ... ORDER BY`, which satisfies the grammar without
            actually limiting anything: 100 percent of the rows always survive, unlike a real `TOP
            (N)` where the ORDER BY at least decides which rows make the cut before being discarded.
            The `ORDER BY`'s only remaining job is to sneak past the compiler, and the resulting
            "ordering" is not guaranteed to survive to any consumer that doesn't apply its own ORDER
            BY - directly confirmed against a real engine: a view built as `SELECT TOP (100) PERCENT
            Id, Amt FROM dbo.T ORDER BY Amt DESC` queried via `SELECT TOP 5 * FROM theView` with no
            outer ORDER BY returned rows in the base table's own storage order, not the view's
            `ORDER BY Amt DESC` - the view's internal ordering was silently and completely discarded.

            This is worse than the merely-unguaranteed case the sibling
            `order-by-not-guaranteed` rule reports: there, the ORDER BY genuinely constrains which
            rows survive TOP/OFFSET-FETCH even though final consumer-visible order isn't promised.
            Here, TOP (100) PERCENT excludes nothing at all, so the ORDER BY provides no guarantee
            whatsoever, at any stage - it exists purely as compiler ceremony, and any consumer
            treating the view as pre-sorted is relying on nothing.
            """,
        HowToFixIt: """
            Remove the TOP (100) PERCENT ... ORDER BY entirely - it does nothing but satisfy the
            grammar rule, so dropping it changes no actual result. Apply an explicit ORDER BY in
            every consuming query that needs a specific row order.
            """,
        Examples:
        [
            new RuleDocExample(
                Title: "A view's TOP (100) PERCENT ORDER BY is silently ignored by a consumer",
                NoncompliantSql: """
                    CREATE VIEW dbo.vRecentOrders AS
                        SELECT TOP (100) PERCENT OrderId, Amount
                        FROM dbo.Orders
                        ORDER BY Amount DESC;

                    -- Consumer:
                    SELECT TOP 5 * FROM dbo.vRecentOrders;
                    """,
                NoncompliantExplanation: "TOP (100) PERCENT excludes zero rows, so the ORDER BY inside the view decides nothing and is not carried through - the consumer's TOP 5 returns whichever 5 rows the engine's own storage order happens to produce, not the 5 highest-amount orders.",
                CompliantSql: """
                    CREATE VIEW dbo.vRecentOrders AS
                        SELECT OrderId, Amount
                        FROM dbo.Orders;

                    -- Consumer:
                    SELECT TOP 5 * FROM dbo.vRecentOrders ORDER BY Amount DESC;
                    """,
                CompliantExplanation: "The meaningless TOP (100) PERCENT ... ORDER BY is removed from the view, and the consumer applies its own explicit ORDER BY - the only place T-SQL actually guarantees row order to a result set."),
        ]);
}
