using SilentScan.Core.Reporting.Sarif;

namespace SilentScan.Core.Reporting.RuleDocs.Lineage;

internal static class MultiReferencedCte
{
    public static string RuleId => SarifRuleCatalog.MultiReferencedCteRuleId;

    public static RuleDocContent Content { get; } = new(
        WhyItMatters: """
            SQL Server does NOT materialize a plain (non-recursive) CTE once and reuse the result
            across every reference - each reference to the same CTE name independently re-runs the
            CTE's own defining query. A CTE referenced twice downstream of its own WITH clause costs
            two full executions of its defining query, referenced three times costs three, and so
            on; unlike a real temp table, which genuinely does materialize its result once, a CTE is
            closer to a named, reusable piece of query TEXT than a cached result set.

            This is a real, well-documented SQL Server behavior, confirmed directly against a real
            engine rather than assumed from documentation or general knowledge: `SET STATISTICS IO
            ON` against a CTE referenced twice showed a base table's own logical-reads count
            doubled, matching two genuinely independent scans of the same underlying data, not one
            materialized-and-reused scan.

            A CTE that references its own name inside its OWN defining query - the recursive-CTE
            anchor/recursive-member shape, since T-SQL has no separate `RECURSIVE` keyword - is never
            counted toward this finding: that self-reference is the structurally mandated recursion
            mechanism itself, not the optional re-invocation this rule targets. Only references
            reachable from OUTSIDE the CTE's own query expression count.
            """,
        HowToFixIt: """
            Materialize the CTE's result into a #temp table or table variable if it's referenced
            more than once, so the defining query runs once instead of once per reference.
            """,
        Examples:
        [
            new RuleDocExample(
                Title: "A CTE referenced twice in the same query",
                NoncompliantSql: """
                    ;WITH RecentOrders AS
                    (
                        SELECT CustomerId, OrderId, Amount
                        FROM dbo.Orders
                        WHERE OrderDate >= DATEADD(day, -30, GETDATE())
                    )
                    SELECT r1.CustomerId, r1.OrderId, r2.OrderId AS AlsoRecentOrderId
                    FROM RecentOrders r1
                    JOIN RecentOrders r2 ON r1.CustomerId = r2.CustomerId AND r1.OrderId <> r2.OrderId;
                    """,
                NoncompliantExplanation: "RecentOrders is referenced twice (r1 and r2) - its defining query, including the full scan/filter of dbo.Orders, runs twice independently rather than once and being reused.",
                CompliantSql: """
                    SELECT CustomerId, OrderId, Amount
                    INTO #RecentOrders
                    FROM dbo.Orders
                    WHERE OrderDate >= DATEADD(day, -30, GETDATE());

                    SELECT r1.CustomerId, r1.OrderId, r2.OrderId AS AlsoRecentOrderId
                    FROM #RecentOrders r1
                    JOIN #RecentOrders r2 ON r1.CustomerId = r2.CustomerId AND r1.OrderId <> r2.OrderId;
                    """,
                CompliantExplanation: "The defining query runs exactly once into a real temp table, which both references then read from directly - no repeated execution of the underlying query."),
        ]);
}
