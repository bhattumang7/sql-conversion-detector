using SilentScan.Core.Reporting.Sarif;

namespace SilentScan.Core.Reporting.RuleDocs.TvfFence;

internal static class CorrelatedApply
{
    public static string RuleId => SarifRuleCatalog.TvfFenceCorrelatedApplyRuleId;

    public static RuleDocContent Content { get; } = new(
        WhyItMatters: """
            A multi-statement table-valued function (a function whose body is a `BEGIN ... END`
            block that populates a `@TableVariable` with one or more separate statements, as
            opposed to a single-statement inline TVF) is opaque to the optimizer in a very specific
            way: the engine cannot see the statements inside the function body when it builds the
            surrounding query's plan. It only knows the function will return *some* rows of the
            declared shape, so it substitutes a fixed, fabricated cardinality estimate for the
            call - 1 row under the legacy cardinality estimator, 100 rows under the 2014+ estimator
            - regardless of how many rows the function body would actually produce. The real
            statements inside the function are only compiled and run when execution reaches that
            operator, as their own separate, un-costed sub-plan.

            `CROSS APPLY`/`OUTER APPLY` against a multi-statement TVF makes this worse than a
            one-time bad estimate: APPLY is inherently row-by-row - it takes the correlated
            parameter (here, `o.CustomerId`) from the outer row and re-evaluates the right side
            once per outer row, the same way a correlated subquery does. Each of those
            per-outer-row evaluations re-enters the function body, re-runs its statements, and pays
            the function's own compile/optimize overhead again, all invisible to the outer plan's
            estimated cost. On a small outer set this is unnoticeable; on an outer set of hundreds
            of thousands of rows it turns into hundreds of thousands of individual sub-executions,
            each cheap in isolation but catastrophic in aggregate - a classic RBAR (row-by-agonizing-row)
            pattern hiding behind ordinary-looking APPLY syntax.
            """,
        HowToFixIt: """
            The durable fix is to stop the per-row re-execution: rewrite the multi-statement TVF as
            a single-statement inline TVF (a function whose body is just `RETURN (SELECT ...)`,
            with no `BEGIN...END` block and no table variable). An inline TVF's single query gets
            substituted directly into the calling query's text before optimization, the same as a
            view would be - so the optimizer costs the whole thing together, picks real cardinality
            estimates from the base tables' statistics, and can flatten the APPLY into a regular
            join when the logic allows it. If the function's logic genuinely requires multiple
            statements (temp state, branching, an explicit loop), the alternative is to inline the
            same logic directly into the calling query - a derived table or CTE driven by the outer
            table - so there's nothing left for the optimizer to treat as an opaque black box.
            """,
        Examples:
        [
            new RuleDocExample(
                Title: "CROSS APPLY re-executing a multi-statement TVF per order",
                NoncompliantSql: """
                    CREATE TABLE dbo.Orders (OrderId INT NOT NULL PRIMARY KEY, CustomerId INT NOT NULL);

                    CREATE FUNCTION dbo.fn_CustomerTier(@CustomerId INT)
                    RETURNS @Tier TABLE (TierName VARCHAR(20))
                    AS
                    BEGIN
                        INSERT INTO @Tier (TierName) SELECT 'Gold';
                        RETURN;
                    END;

                    SELECT o.OrderId, t.TierName
                    FROM dbo.Orders o
                    CROSS APPLY dbo.fn_CustomerTier(o.CustomerId) t;
                    """,
                NoncompliantExplanation: "The optimizer estimates a fixed row count for fn_CustomerTier's output and cannot see inside its body - and because it's driven by CROSS APPLY, the function body re-runs once per row of Orders, not once for the whole query.",
                CompliantSql: """
                    CREATE FUNCTION dbo.fn_CustomerTier(@CustomerId INT)
                    RETURNS TABLE
                    AS
                    RETURN (SELECT 'Gold' AS TierName);

                    SELECT o.OrderId, t.TierName
                    FROM dbo.Orders o
                    CROSS APPLY dbo.fn_CustomerTier(o.CustomerId) t;
                    """,
                CompliantExplanation: "As an inline TVF, the RETURN's query is substituted into the outer query before optimization - the optimizer sees and costs the real logic together with Orders instead of treating it as an opaque per-row call."),
        ]);
}
