using SilentScan.Core.Reporting.Sarif;

namespace SilentScan.Core.Reporting.RuleDocs.TriggerCorrectness;

internal static class MultiRowUnsafeSingleRowAssignment
{
    public static string RuleId => SarifRuleCatalog.TriggerCorrectnessMultiRowUnsafeSingleRowAssignmentRuleId;

    public static RuleDocContent Content { get; } = new(
        WhyItMatters: """
            A DML trigger in SQL Server fires once per statement, not once per row. Every INSERT,
            UPDATE, or DELETE that touches the trigger's table populates the inserted and/or
            deleted pseudo-tables with one row for every row the statement actually affected - a
            single-row INSERT gives you one row in inserted, but a multi-row INSERT (a VALUES list
            with several tuples, an INSERT ... SELECT pulling in a batch, a bulk load) gives you
            just as many rows in inserted as the statement touched. Nothing about the trigger body
            changes based on how many rows fired it; the same code runs either way.

            A statement like SELECT @Id = Id FROM inserted has no WHERE clause narrowing it to one
            row, no TOP, no aggregate - it is a bare scalar assignment against a table-shaped
            result set. When inserted holds exactly one row this behaves exactly as the author
            expects. When inserted holds more than one row, SQL Server does not raise an error: a
            scalar variable assignment from a multi-row SELECT silently executes once per row in
            an unspecified order determined by the engine's own access path, and the variable is
            left holding whichever row's value happened to be assigned last. There's no guarantee
            it's the first row, the last row by any business-meaningful ordering, or even the same
            row from one execution to the next - it depends on plan shape, which can itself change
            with statistics or an index rebuild.

            This is one of the most common production bugs in T-SQL specifically because it is
            invisible in testing: single-row INSERT/UPDATE statements (the shape most manual
            testing and most ORM default behavior uses) never expose the bug, since there's only
            ever one row to pick. The failure mode only appears the first time a genuine multi-row
            batch - a bulk import, a multi-row application INSERT, an UPDATE with a WHERE clause
            matching several rows - fires the trigger, at which point (n-1) rows' worth of data
            silently never reaches whatever the variable was driving, with no error and no warning
            row-count mismatch to notice.
            """,
        HowToFixIt: """
            Replace the single-row variable assignment with set-based logic that processes every
            row of inserted/deleted, not just whichever one the engine happened to assign last.
            Where the goal is to do something per affected row, join inserted (and/or deleted) to
            the target table, or to another table, so the statement operates on the whole row set
            at once. Where truly row-by-row procedural logic is unavoidable, use an explicit cursor
            over inserted so every row is visited in a controlled, understood order rather than
            relying on an unspecified assignment order.
            """,
        Examples:
        [
            new RuleDocExample(
                Title: "A single scalar variable captures one arbitrary row out of a multi-row insert",
                NoncompliantSql: """
                    CREATE TABLE dbo.Orders
                    (
                        OrderId    INT           NOT NULL PRIMARY KEY,
                        CustomerId INT           NOT NULL,
                        Total      DECIMAL(10,2) NOT NULL
                    );

                    CREATE TABLE dbo.OrderAudit
                    (
                        OrderId    INT           NOT NULL,
                        LoggedAt   DATETIME2     NOT NULL DEFAULT SYSUTCDATETIME()
                    );

                    CREATE TRIGGER dbo.trg_Orders_Insert ON dbo.Orders
                    AFTER INSERT
                    AS
                    BEGIN
                        DECLARE @OrderId INT;
                        SELECT @OrderId = OrderId FROM inserted;

                        INSERT INTO dbo.OrderAudit (OrderId)
                        VALUES (@OrderId);
                    END;
                    """,
                NoncompliantExplanation: "When the caller's INSERT supplies more than one row in a single statement (a multi-row VALUES list, or an INSERT ... SELECT), @OrderId is assigned once per row of inserted in an unspecified order and ends up holding only the last-assigned row's id - the other rows are never audited, with no error raised.",
                CompliantSql: """
                    CREATE TRIGGER dbo.trg_Orders_Insert ON dbo.Orders
                    AFTER INSERT
                    AS
                    BEGIN
                        INSERT INTO dbo.OrderAudit (OrderId)
                        SELECT OrderId FROM inserted;
                    END;
                    """,
                CompliantExplanation: "The INSERT ... SELECT is set-based over the whole of inserted, so every row the firing statement affected - one or a thousand - is audited in the same execution."),
        ]);
}
