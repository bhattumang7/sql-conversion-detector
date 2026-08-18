using SilentScan.Core.Reporting.Sarif;

namespace SilentScan.Core.Reporting.RuleDocs.TriggerCorrectness;

internal static class UpdateFunctionWithoutValueComparison
{
    public static string RuleId => SarifRuleCatalog.TriggerCorrectnessUpdateFunctionWithoutValueComparisonRuleId;

    public static RuleDocContent Content { get; } = new(
        WhyItMatters: """
            Inside an INSERT/UPDATE trigger, UPDATE(column_name) is a purely syntactic check: it
            returns true when the named column appeared in the SET list of the UPDATE statement (or
            is one of the columns targeted by an INSERT), and false otherwise. It says nothing at
            all about whether the value actually changed - it cannot, because by the time the
            trigger runs, all it has to work with is the fact that the column was named as an
            assignment target. UPDATE(column_name) = TRUE and "the value of column_name changed"
            are two different questions that happen to coincide often enough in hand-written SQL
            that many authors never notice they aren't the same question.

            They stop coinciding the moment a full-column UPDATE enters the picture, which is
            exactly what most ORMs (Entity Framework, Hibernate, Dapper-based repositories using a
            generated update, and similar) issue by default: a single UPDATE statement that SETs
              every mapped column of the row, regardless of which properties the application code
            actually changed on the in-memory object. When an ORM saves an entity where only one
            field changed, the resulting UPDATE still names every column in its SET list - so
            UPDATE(column_name) reports true for every column on every save, including the ones
            whose value is identical before and after. A trigger written as IF UPDATE(Status) ...
            to run "only when Status changes" instead runs on every single save that touches the
            row at all, whether or not Status itself moved.

            The practical consequence is a trigger that fires its full logic - an audit insert, a
            notification, a cascading recalculation, a workflow transition - on every no-op save,
            not just genuine changes. For a workflow-transition trigger this can mean re-running
            business logic that assumed it only executes on a real state change; for an audit
            trigger it means logging rows with identical old and new values, indistinguishable in
            the audit log from a real change unless someone thinks to compare the logged values
            themselves.
            """,
        HowToFixIt: """
            Compare inserted.column against deleted.column directly (accounting for NULLs, since a
            plain <> comparison never evaluates true when either side is NULL) instead of relying
            on UPDATE(column) alone. A safe pattern is
            NOT EXISTS (SELECT i.Status FROM inserted i EXCEPT SELECT d.Status FROM deleted d) or
            the equivalent using IS DISTINCT FROM in engine versions that support it, either of
            which treats NULL-to-NULL as unchanged and any other difference as changed. Keep
            UPDATE(column) if it's still useful as a cheap pre-filter for a multi-column trigger,
            but gate the actual logic on the value comparison, not the syntactic check alone.
            """,
        Examples:
        [
            new RuleDocExample(
                Title: "UPDATE(Status) fires on every ORM save, not just a status change",
                NoncompliantSql: """
                    CREATE TABLE dbo.Orders
                    (
                        OrderId INT         NOT NULL PRIMARY KEY,
                        Status  VARCHAR(20) NOT NULL,
                        Notes   VARCHAR(200) NULL
                    );

                    CREATE TABLE dbo.StatusHistory
                    (
                        OrderId   INT         NOT NULL,
                        OldStatus VARCHAR(20) NOT NULL,
                        NewStatus VARCHAR(20) NOT NULL,
                        ChangedAt DATETIME2   NOT NULL DEFAULT SYSUTCDATETIME()
                    );

                    CREATE TRIGGER dbo.trg_Orders_StatusChange ON dbo.Orders
                    AFTER UPDATE
                    AS
                    BEGIN
                        IF UPDATE(Status)
                        BEGIN
                            INSERT INTO dbo.StatusHistory (OrderId, OldStatus, NewStatus)
                            SELECT i.OrderId, d.Status, i.Status
                            FROM inserted AS i
                            JOIN deleted AS d ON d.OrderId = i.OrderId;
                        END;
                    END;
                    """,
                NoncompliantExplanation: "An ORM that issues a full-column UPDATE on every save (SET Status = @p0, Notes = @p1, ...) names Status in the SET list every time, even when only Notes actually changed - UPDATE(Status) is true on every such save, and the trigger logs a spurious status-history row with identical OldStatus and NewStatus.",
                CompliantSql: """
                    CREATE TRIGGER dbo.trg_Orders_StatusChange ON dbo.Orders
                    AFTER UPDATE
                    AS
                    BEGIN
                        IF UPDATE(Status)
                        BEGIN
                            INSERT INTO dbo.StatusHistory (OrderId, OldStatus, NewStatus)
                            SELECT i.OrderId, d.Status, i.Status
                            FROM inserted AS i
                            JOIN deleted AS d ON d.OrderId = i.OrderId
                            WHERE d.Status <> i.Status;
                        END;
                    END;
                    """,
                CompliantExplanation: "The added WHERE d.Status <> i.Status compares the actual before/after values, so a save that named Status in its SET list without changing its value no longer produces a history row."),
        ]);
}
