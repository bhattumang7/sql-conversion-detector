using SilentScan.Core.Reporting.Sarif;

namespace SilentScan.Core.Reporting.RuleDocs.ForcedSerial;

internal static class FastForwardCursor
{
    public static string RuleId => SarifRuleCatalog.ForcedSerialFastForwardCursorRuleId;

    public static RuleDocContent Content { get; } = new(
        WhyItMatters: """
            A cursor declared FAST_FORWARD - or, equivalently, one declared FORWARD_ONLY READ_ONLY
            without an explicit STATIC/KEYSET/DYNAMIC (which SQL Server also optimizes as a
            fast-forward cursor) - asks the engine for a forward-only, read-only cursor optimized
            for the cheapest possible per-row FETCH. To deliver that guarantee, the engine forces
            the cursor's own defining SELECT to run with a serial plan; a real executed plan for
            such a query shows NonParallelPlanReason = "NoParallelFastForwardCursor" on the plan
            root. The restriction is specifically about being able to fetch rows one at a time,
            in order, with minimal overhead per fetch - a guarantee a parallel plan's
              multi-threaded, buffered row production can't provide as cheaply.

            This runs directly against the most common cursor-related advice in T-SQL: "if you must
            use a cursor, always declare it LOCAL FAST_FORWARD" is correct and remains correct for
            what it's optimizing - the per-row FETCH cost inside the cursor loop, which
            FAST_FORWARD genuinely minimizes better than any other cursor type. What that advice
            doesn't cover is the defining SELECT itself: the query that determines what the cursor's
            rows are in the first place. That query pays the FAST_FORWARD tax up front, once, as a
            forced-serial compile and execution, before the first row is ever fetched. FAST_FORWARD
            makes the cursor's row-by-row loop cheap; it does not make the cursor's underlying query
            cheap, and can make that underlying query's own execution slower than the same query run
            as a plain SELECT with a parallel plan available.

            This tension is invisible in the DECLARE CURSOR statement itself - nothing about the
            syntax signals which of the two costs is going to dominate for a given query. A cursor
            over a small, cheap lookup pays a negligible serial-plan cost either way; a cursor whose
            defining SELECT does a large scan, join, or aggregation is where the forced-serial
            restriction on that one query can genuinely matter.
            """,
        HowToFixIt: """
            There's no cursor-type substitution that resolves this - forcing the defining query
            serial is inherent to what FAST_FORWARD is. If the cursor's defining SELECT is itself
            expensive enough that its own parallelism loss is a measured problem, the real fix is
            almost always to avoid the cursor altogether and rewrite the row-by-row logic as a
            set-based statement (a single UPDATE/INSERT joined to the source set, a windowed query,
            or a MERGE), which removes both the cursor's per-row FETCH overhead and the defining
            query's forced-serial restriction at once. Switching to a different cursor type only
            trades one cost for another - it does not remove the forced-serial restriction from a
            FAST_FORWARD-shaped declaration, since the restriction follows from the read-only
            forward-only guarantee itself.
            """,
        Examples:
        [
            new RuleDocExample(
                Title: "A FAST_FORWARD cursor's defining query loses parallelism",
                NoncompliantSql: """
                    CREATE TABLE dbo.Orders
                    (
                        OrderId    INT           NOT NULL PRIMARY KEY,
                        CustomerId INT           NOT NULL,
                        Total      DECIMAL(10,2) NOT NULL,
                        Region     VARCHAR(20)   NOT NULL
                    );

                    DECLARE @OrderId INT, @Total DECIMAL(10,2);

                    DECLARE order_cursor CURSOR LOCAL FAST_FORWARD FOR
                        SELECT OrderId, Total
                        FROM dbo.Orders
                        WHERE Region = 'EMEA';

                    OPEN order_cursor;
                    FETCH NEXT FROM order_cursor INTO @OrderId, @Total;

                    WHILE @@FETCH_STATUS = 0
                    BEGIN
                        -- per-row processing
                        FETCH NEXT FROM order_cursor INTO @OrderId, @Total;
                    END;

                    CLOSE order_cursor;
                    DEALLOCATE order_cursor;
                    """,
                NoncompliantExplanation: "The SELECT ... WHERE Region = 'EMEA' defining this cursor is forced onto a serial plan by the FAST_FORWARD declaration - on a large dbo.Orders table this defining scan can be slower than the same SELECT run standalone with a parallel plan available, independent of how cheap the row-by-row FETCH loop itself is.",
                CompliantSql: """
                    UPDATE o
                    SET o.Total = o.Total -- per-row logic expressed set-based
                    FROM dbo.Orders AS o
                    WHERE o.Region = 'EMEA';
                    """,
                CompliantExplanation: "Rewriting the row-by-row loop as a single set-based statement removes the cursor - and with it both the per-row FETCH overhead and the forced-serial restriction on the underlying query - letting the optimizer parallelize the whole operation if it judges that worthwhile."),
        ]);
}
