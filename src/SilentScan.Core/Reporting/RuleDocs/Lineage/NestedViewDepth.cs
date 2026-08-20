using SilentScan.Core.Reporting.Sarif;

namespace SilentScan.Core.Reporting.RuleDocs.Lineage;

internal static class NestedViewDepth
{
    public static string RuleId => SarifRuleCatalog.NestedViewDepthRuleId;

    public static RuleDocContent Content { get; } = new(
        WhyItMatters: """
            A view or inline TVF nested two or more view/TVF layers deep before reaching a real base
            table is a structural maintenance and robustness risk, not necessarily a current
            performance one - a query through it may still seek perfectly fine today. The risk grows
            with depth for a different reason: a change to a base table now has to be traced through
            two or more independent view layers before its real blast radius is understood, and each
            of those layers is an independent place a `SELECT *`/column-list mismatch or a silent
            type-widening conversion can hide, invisible from any single layer's own definition.

            This is a catalog/lineage-only, unconditional fact - reported once per view/inline TVF
            whose own definition crosses the depth threshold, independent of whether any scanned
            query actually calls it, the same way a structural risk fact is reported regardless of
            current usage elsewhere in this tool. The threshold is depth ≥ 2: measured against a
            real production-shaped test database, depth 1 view-over-view nesting is common and not
            itself notable, while depth ≥ 2 was a small, genuinely selective signal rather than
            flagging every view that happens to sit over another view at all.
            """,
        Examples:
        [
            new RuleDocExample(
                Title: "A view nested two layers deep before reaching a base table",
                NoncompliantSql: """
                    CREATE VIEW dbo.vw_Orders AS
                        SELECT OrderId, CustomerId, Amount FROM dbo.OrdersRaw;

                    CREATE VIEW dbo.vw_OrdersWithCustomer AS
                        SELECT o.OrderId, o.Amount, c.CustomerName
                        FROM dbo.vw_Orders o
                        JOIN dbo.Customers c ON c.CustomerId = o.CustomerId;

                    CREATE VIEW dbo.vw_RecentOrdersSummary AS
                        SELECT CustomerName, SUM(Amount) AS TotalAmount
                        FROM dbo.vw_OrdersWithCustomer
                        GROUP BY CustomerName;
                    """,
                NoncompliantExplanation: "dbo.vw_RecentOrdersSummary sits two view layers above the real base tables (through vw_OrdersWithCustomer, then vw_Orders, down to dbo.OrdersRaw/dbo.Customers) - a change to OrdersRaw's own column list has to be traced through both intermediate views before its effect on this view is understood."),
        ]);
}
