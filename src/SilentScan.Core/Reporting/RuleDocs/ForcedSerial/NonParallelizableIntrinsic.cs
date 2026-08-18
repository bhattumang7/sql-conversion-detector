using SilentScan.Core.Reporting.Sarif;

namespace SilentScan.Core.Reporting.RuleDocs.ForcedSerial;

internal static class NonParallelizableIntrinsic
{
    public static string RuleId => SarifRuleCatalog.ForcedSerialNonParallelizableIntrinsicRuleId;

    public static RuleDocContent Content { get; } = new(
        WhyItMatters: """
            A small, oracle-confirmed set of built-in functions and globals - OBJECT_ID,
            IDENT_CURRENT, ERROR_NUMBER, ERROR_MESSAGE, ERROR_LINE, ERROR_SEVERITY, ERROR_STATE,
            ERROR_PROCEDURE, and @@TRANCOUNT - read state that is scoped to the current session or
            the current transaction: the active error context of a TRY/CATCH block, the current
            transaction nesting depth, or metadata lookups whose result can depend on session-level
            settings. That state isn't something the engine can hand out consistently to several
            parallel worker threads evaluating the same query at once - each thread would need a
            coherent, identical view of session/transaction-scoped state that was never designed to
            be read concurrently from multiple execution contexts within one statement.

            Rather than risk a race or an inconsistent read across worker threads, SQL Server simply
            forces serial execution for any query that references one of these functions from within
            a real FROM clause - a query actually touching a table or view, not just a bare SELECT
            ERROR_MESSAGE() with no FROM. A real executed plan for such a query shows
            NonParallelPlanReason = "NonParallelizableIntrinsicFunction" on the plan root, confirming
            the specific mechanism rather than leaving it to guesswork from the plan shape alone.

            This most often shows up unintentionally inside a CATCH block that logs error details by
            joining a diagnostics table against something computed from ERROR_MESSAGE()/
            ERROR_NUMBER(), or in a query that calls OBJECT_ID() or IDENT_CURRENT() per row (e.g. in
            a computed column expression or a correlated subquery) rather than once up front. In both
            cases the author's intent was almost always just "read this one session-scoped value,"
            not "make the engine evaluate it fresh once per parallel worker" - the forced-serial
            behavior is a side effect of where the call sits in the query, not of anything the author
            was trying to achieve.
            """,
        HowToFixIt: """
            Compute the intrinsic's value into a variable before the query runs, then reference the
            variable inside the query instead of calling the function directly. This changes nothing
            about the value the query sees - it's evaluated once, at the same point in execution,
            either way - but removes the function call from the query's own execution plan, so the
            NonParallelizableIntrinsicFunction restriction no longer applies to that query and the
            optimizer is free to consider a parallel plan for it.
            """,
        Examples:
        [
            new RuleDocExample(
                Title: "ERROR_MESSAGE() referenced inside a query forces it serial",
                NoncompliantSql: """
                    CREATE TABLE dbo.ErrorLog
                    (
                        LogId     INT IDENTITY PRIMARY KEY,
                        OrderId   INT           NOT NULL,
                        ErrorText VARCHAR(4000) NOT NULL
                    );

                    CREATE TABLE dbo.PendingOrders
                    (
                        OrderId INT NOT NULL PRIMARY KEY,
                        Total   DECIMAL(10,2) NOT NULL
                    );

                    BEGIN TRY
                        UPDATE dbo.PendingOrders SET Total = Total / 0;
                    END TRY
                    BEGIN CATCH
                        INSERT INTO dbo.ErrorLog (OrderId, ErrorText)
                        SELECT OrderId, ERROR_MESSAGE()
                        FROM dbo.PendingOrders;
                    END CATCH;
                    """,
                NoncompliantExplanation: "ERROR_MESSAGE() is called once per row inside the SELECT's FROM-clause query over dbo.PendingOrders, forcing that query onto a serial plan even though it would otherwise be a plain scan eligible for parallelism.",
                CompliantSql: """
                    BEGIN TRY
                        UPDATE dbo.PendingOrders SET Total = Total / 0;
                    END TRY
                    BEGIN CATCH
                        DECLARE @ErrorText VARCHAR(4000) = ERROR_MESSAGE();

                        INSERT INTO dbo.ErrorLog (OrderId, ErrorText)
                        SELECT OrderId, @ErrorText
                        FROM dbo.PendingOrders;
                    END CATCH;
                    """,
                CompliantExplanation: "ERROR_MESSAGE() is evaluated once into @ErrorText before the query runs; the query itself now only references a plain variable, so it's no longer subject to the NonParallelizableIntrinsicFunction restriction."),
        ]);
}
