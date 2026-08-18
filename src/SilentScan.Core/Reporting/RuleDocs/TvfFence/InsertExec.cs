using SilentScan.Core.Reporting.Sarif;

namespace SilentScan.Core.Reporting.RuleDocs.TvfFence;

internal static class InsertExec
{
    public static string RuleId => SarifRuleCatalog.TvfFenceInsertExecRuleId;

    public static RuleDocContent Content { get; } = new(
        WhyItMatters: """
            `INSERT ... EXEC` (capturing a stored procedure's result set straight into a table) is
            fenced from the optimizer even more completely than a multi-statement TVF: the
            procedure being executed can itself be an arbitrary batch of statements, conditionally
            branching, calling further procedures, and the engine has no way to know in advance how
            many rows it will ultimately produce or what their content will be. To make the insert
            transactionally safe against that uncertainty, SQL Server forces an Eager Spool: the
            entire result set coming back from the EXEC is first materialized into a hidden
            worktable in tempdb, and only once that spool has fully drained does the actual INSERT
            into the target table begin. That's pure overhead - a full extra write-then-read pass
            over the whole result set - paid on every execution regardless of how small the result
            turns out to be.

            The other constraint is structural, not just a cost one: `INSERT ... EXEC` cannot be
            nested. A procedure that itself runs `INSERT ... EXEC` against a second procedure fails
            outright ("An INSERT EXEC statement cannot be nested"), so this pattern silently caps how
            the containing procedure can be composed or reused from another `INSERT ... EXEC` call
            site - a limitation that only surfaces the first time someone tries to nest it, not when
            the original procedure was written.
            """,
        HowToFixIt: """
            Where the source is (or can be turned into) a query rather than a batch of procedural
            statements, replace `INSERT ... EXEC` with `INSERT INTO target (...) SELECT ... FROM
            source` directly, or with an inline TVF called in the FROM clause - both let the
            optimizer see and cost the real source query instead of treating it as an opaque
            black-box result set requiring a spool. Where the source genuinely has to be a stored
            procedure (multiple statements, branching, dynamic SQL), OUTPUT parameters or a table
            type passed by reference are usually a better transport than capturing the result set
            through EXEC, since they avoid the spool-then-drain sequencing entirely.
            """,
        Examples:
        [
            new RuleDocExample(
                Title: "INSERT ... EXEC forces an Eager Spool before the insert can start",
                NoncompliantSql: """
                    CREATE TABLE dbo.Staging (OrderId INT NOT NULL);

                    CREATE PROCEDURE dbo.usp_GetOrderIds
                    AS
                    BEGIN
                        SELECT 1 AS OrderId;
                    END;

                    INSERT INTO dbo.Staging (OrderId)
                    EXEC dbo.usp_GetOrderIds;
                    """,
                NoncompliantExplanation: "The engine cannot know in advance what usp_GetOrderIds will return, so the whole result set is spooled to a tempdb worktable first; the INSERT into Staging only begins once that spool has fully drained.",
                CompliantSql: """
                    CREATE TABLE dbo.Staging (OrderId INT NOT NULL);
                    CREATE TABLE dbo.Orders (OrderId INT NOT NULL PRIMARY KEY);

                    INSERT INTO dbo.Staging (OrderId)
                    SELECT OrderId FROM dbo.Orders;
                    """,
                CompliantExplanation: "With the source expressed as a query instead of a procedure call, the optimizer sees the real source and can insert directly, without a mandatory spool-then-drain sequencing."),
        ]);
}
