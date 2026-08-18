using SilentScan.Core.Reporting.Sarif;

namespace SilentScan.Core.Reporting.RuleDocs.ForcedSerial;

internal static class TableVariableModification
{
    public static string RuleId => SarifRuleCatalog.ForcedSerialTableVariableModificationRuleId;

    public static RuleDocContent Content { get; } = new(
        WhyItMatters: """
            A table variable, unlike a #temp table, participates in the surrounding batch's
            transaction in a way SQL Server's parallel execution engine specifically can't
            parallelize a write into. When a statement's write target is a DECLARE'd table
            variable - a plain INSERT/UPDATE/DELETE/MERGE against it, or a DML statement's OUTPUT
            clause with an INTO pointing at one - the engine forces that statement's own plan to run
            with a single thread, regardless of how large the read side of the statement is or how
            many parallel workers would otherwise be available. This shows up in a real executed
            plan as NonParallelPlanReason = "TableVariableTransactionsDoNotSupportParallelNested
            Transaction" on the plan's root, a directly diagnostic string rather than something
            inferred from serial operators alone.

            The restriction is specific to the write, not to the table variable's mere existence in
            the statement: a query that only reads from a table variable - joins against it, selects
            from it - is not subject to this restriction and can still get a parallel plan. It is
            the act of being the target of a modification (or an OUTPUT ... INTO) that forces that
            one statement serial. A batch with several statements, only one of which writes to the
            table variable, only pays the serial cost on that one statement; the others are
            unaffected.

            For a small table variable holding a handful of rows this is invisible - a serial plan
            over ten rows costs nothing measurable either way. It becomes a real cost only when the
            statement doing the write also does substantial work that would otherwise benefit from
            parallelism - a large read, join, or aggregation feeding the rows being written -
            because that entire statement is pinned to one thread for the write's sake, not just the
            insert/update/delete operator itself.
            """,
        HowToFixIt: """
            If the statement's read side is large enough that parallelism would meaningfully help
            and the forced-serial plan is a measured cost worth avoiding, replace the table variable
            with a #temp table for that specific write - #temp tables don't carry the same
            parallel-nested-transaction restriction and can get a parallel plan for the same
            workload. This isn't a blanket recommendation to avoid table variables generally; where
            the row counts involved are small, or the write isn't on a hot path, the forced-serial
            plan is simply a cost to be aware of, not something worth restructuring around.
            """,
        Examples:
        [
            new RuleDocExample(
                Title: "Writing into a table variable forces the whole statement serial",
                NoncompliantSql: """
                    CREATE TABLE dbo.Orders
                    (
                        OrderId    INT           NOT NULL PRIMARY KEY,
                        CustomerId INT           NOT NULL,
                        Total      DECIMAL(10,2) NOT NULL,
                        OrderDate  DATETIME2     NOT NULL
                    );

                    DECLARE @HighValueOrders TABLE (OrderId INT NOT NULL, Total DECIMAL(10,2) NOT NULL);

                    INSERT INTO @HighValueOrders (OrderId, Total)
                    SELECT OrderId, Total
                    FROM dbo.Orders
                    WHERE OrderDate >= '20240101'
                      AND Total > 1000;
                    """,
                NoncompliantExplanation: "Even though the SELECT scanning dbo.Orders could otherwise use a parallel plan, @HighValueOrders being the INSERT's write target forces the entire statement - the scan included - onto a single thread.",
                CompliantSql: """
                    CREATE TABLE #HighValueOrders (OrderId INT NOT NULL, Total DECIMAL(10,2) NOT NULL);

                    INSERT INTO #HighValueOrders (OrderId, Total)
                    SELECT OrderId, Total
                    FROM dbo.Orders
                    WHERE OrderDate >= '20240101'
                      AND Total > 1000;
                    """,
                CompliantExplanation: "A #temp table does not carry the parallel-nested-transaction restriction, so the SELECT feeding the insert is free to use a parallel plan when the optimizer judges it worthwhile."),
        ]);
}
