using SilentScan.Core.Predicates;
using SilentScan.Core.Reporting.Sarif;

namespace SilentScan.Core.Reporting.RuleDocs.QueryAntiPattern;

internal static class TableVariableLowCompatEstimate
{
    public static string RuleId => SarifRuleCatalog.QueryAntiPatternRuleId(QueryAntiPatternFindingKind.TableVariableLowCompatEstimate);

    public static RuleDocContent Content { get; } = new(
        WhyItMatters: """
            A table variable is declared with DECLARE @t TABLE(...), not SELECT/INSERT INTO a
            #temp table, and that difference is more than syntax: below database compatibility
            level 150, SQL Server never maintains column-level statistics for a table variable at
            all. When the optimizer compiles a query that reads from the table variable, it has no
            histogram, no density information, nothing to estimate a row count from - so it falls
            back to a fixed guess of exactly 1 row, regardless of whether the table variable was
            just loaded with 10 rows or 10 million. This scan verifies the fixed-1-row estimate
            against the actual plan XML rather than assuming it from the DECLARE alone, because the
            behavior is compatibility-level-gated, not table-variable-gated in an absolute sense.

            The 1-row estimate cascades into every operator downstream of it. A join against the
            table variable gets sized for a 1-row build side and the optimizer picks a nested loops
            join - fine at genuinely small row counts, catastrophic once the table variable holds
            thousands of rows and the loop runs once per outer row. Memory grants for spills and
            sorts are sized off the same 1-row guess, so a sort that needs gigabytes gets a grant
            for kilobytes and spills to tempdb. None of this shows up in the source text; the query
            looks identical whether it runs against 1 row or 1 million, and only the execution plan
            reveals the fixed estimate.

            SQL Server 2019 (compatibility level 150) introduced table variable deferred
            compilation specifically to close this gap: the statement that first reads a table
            variable is compiled after the variable has actually been populated, so the optimizer
            gets a real row count for that read, the same way it already did for #temp tables. This
            finding only fires below that compatibility level, where deferred compilation isn't
            available and the fixed-1-row estimate is unavoidable for any table variable read.
            """,
        HowToFixIt: """
            Where raising the database's compatibility level to 150 or higher is possible, that
            alone fixes this for every table variable in the database - deferred compilation kicks
            in automatically, no code changes required. Where the compatibility level is pinned
            below 150 for another reason (an application dependency on older cardinality estimator
            behavior, for instance), replace the table variable with a #temp table for any query
            path where the row count materially affects the plan: #temp tables get real
            column statistics and an accurate cardinality estimate at every compatibility level,
            at the cost of the transactional/scoping differences between the two object types.
            """,
        Examples:
        [
            new RuleDocExample(
                Title: "A table variable loaded with thousands of rows, estimated at 1",
                NoncompliantSql: """
                    -- Database compatibility level 140 (SQL Server 2017) or lower.
                    CREATE TABLE dbo.Orders
                    (
                        OrderId    INT      NOT NULL PRIMARY KEY,
                        CustomerId INT      NOT NULL,
                        OrderDate  DATETIME NOT NULL
                    );

                    DECLARE @RecentOrders TABLE (OrderId INT NOT NULL PRIMARY KEY);

                    INSERT INTO @RecentOrders (OrderId)
                    SELECT OrderId
                    FROM dbo.Orders
                    WHERE OrderDate >= DATEADD(DAY, -30, GETDATE());

                    SELECT o.OrderId, o.CustomerId
                    FROM dbo.Orders AS o
                    JOIN @RecentOrders AS r ON r.OrderId = o.OrderId;
                    """,
                NoncompliantExplanation: "At compatibility level 140, the optimizer compiles the join against @RecentOrders assuming it holds exactly 1 row, no matter how many rows the preceding INSERT actually loaded - the plan XML shows an EstimatedRows of 1 on that side even when thousands of rows are present, and a nested loops join gets picked that would be wrong at real volume.",
                CompliantSql: """
                    ALTER DATABASE CURRENT SET COMPATIBILITY_LEVEL = 150;

                    -- Same batch as above, now compiled with table variable deferred compilation.
                    """,
                CompliantExplanation: "At compatibility level 150+, the statement reading @RecentOrders is deferred-compiled after the INSERT has populated it, so the optimizer estimates the real row count instead of a fixed 1."),
        ]);
}
