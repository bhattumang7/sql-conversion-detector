using SilentScan.Core.Reporting.Sarif;

namespace SilentScan.Core.Reporting.RuleDocs.TvfFence;

internal static class FromOrJoin
{
    public static string RuleId => SarifRuleCatalog.TvfFenceFromOrJoinRuleId;

    public static RuleDocContent Content { get; } = new(
        WhyItMatters: """
            A multi-statement table-valued function referenced directly in a `FROM` or `JOIN`
            clause - not via APPLY, just an ordinary join to its result set - hits the same
            optimizer blind spot as any other MSTVF call: the engine can't see the statements
            inside the function body, so it substitutes a fixed, fabricated cardinality estimate
            for the function's output (1 row legacy CE / 100 rows 2014+ CE) instead of a real one
            derived from statistics.

            What makes the FROM/JOIN shape distinct from a correlated APPLY is where the damage
            lands: there's no per-row re-execution here, because the function isn't being driven by
            a correlated parameter from another table - it runs once. The cost instead is that the
            bad estimate poisons the *surrounding join's* plan choice. The optimizer picks a join
            algorithm (nested loops, hash, merge) and a join order based on the estimated row counts
            on each side, and a function estimated at 1 or 100 rows when it actually returns tens of
            thousands sends the optimizer looking for a nested-loops join against what it believes
            is a tiny input - the worst-case algorithm for what turns out to be a large one, with no
            spill protection and no re-costing once the true size is known.
            """,
        HowToFixIt: """
            Rewrite the multi-statement TVF as a single-statement inline TVF (`RETURN (SELECT ...)`,
            no `BEGIN...END` block, no table variable) so its defining query expands into the join
            before optimization and the optimizer can derive a real estimate for it, the same way it
            would for a table or view reference. Where the function's logic genuinely needs multiple
            statements, inline that logic as a derived table or CTE in the calling query instead of
            calling out to a function at all.
            """,
        Examples:
        [
            new RuleDocExample(
                Title: "A multi-statement TVF joined directly in FROM",
                NoncompliantSql: """
                    CREATE TABLE dbo.Orders (OrderId INT NOT NULL PRIMARY KEY, CustomerId INT NOT NULL);

                    CREATE FUNCTION dbo.fn_OrderLines(@OrderId INT)
                    RETURNS @Lines TABLE (LineId INT, Qty INT)
                    AS
                    BEGIN
                        INSERT INTO @Lines (LineId, Qty) SELECT 1, 1;
                        RETURN;
                    END;

                    SELECT o.OrderId, l.LineId
                    FROM dbo.Orders o
                    JOIN dbo.fn_OrderLines(1) l ON l.LineId = o.OrderId;
                    """,
                NoncompliantExplanation: "The optimizer estimates fn_OrderLines' output at a fixed row count regardless of how many rows the function body actually inserts into @Lines - if the real count is much larger, the join algorithm chosen for the surrounding query is picked for the wrong input size.",
                CompliantSql: """
                    CREATE FUNCTION dbo.fn_OrderLines(@OrderId INT)
                    RETURNS TABLE
                    AS
                    RETURN (SELECT LineId, Qty FROM dbo.OrderLineStaging WHERE OrderId = @OrderId);

                    SELECT o.OrderId, l.LineId
                    FROM dbo.Orders o
                    JOIN dbo.fn_OrderLines(1) l ON l.LineId = o.OrderId;
                    """,
                CompliantExplanation: "As an inline TVF, the function's query expands into the join before optimization - the optimizer derives a real cardinality estimate from OrderLineStaging's own statistics instead of guessing."),
        ]);
}
