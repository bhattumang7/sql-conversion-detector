using SilentScan.Core.Predicates;
using SilentScan.Core.Reporting.Sarif;

namespace SilentScan.Core.Reporting.RuleDocs.ControlFlow;

internal static class TriggerEmitsOutput
{
    public static string RuleId => SarifRuleCatalog.ControlFlowRiskRuleId(ControlFlowRiskFindingKind.TriggerEmitsOutput);

    public static RuleDocContent Content { get; } = new(
        WhyItMatters: """
            A `SELECT` with a real (non-assignment-only) result set, or a `PRINT`, appearing directly
            inside a `CREATE`/`ALTER TRIGGER` body sends that output back to whatever connection
            happened to fire the triggering DML statement - not the application code that originally
            issued the request that led here. This is a well-documented antipattern: the caller that
            triggered the DML has no idea a trigger even ran, let alone that it emitted a result set
            or a message, so the output is either silently ignored (most client libraries don't
            expect an extra, unrequested result set from a plain INSERT/UPDATE/DELETE) or actively
            confuses whatever tool happens to be connected and reading results in order.

            A `SELECT @x = expr` or `SELECT ... INTO` assignment-only form sends no client-visible
            result set at all and correctly never fires - only a real, row-returning SELECT or a
            PRINT counts. This rule only inspects a trigger's own top-level body; a statement inside
            a procedure the trigger merely calls is not chased, since this pass doesn't hold every
            module's parsed AST alive simultaneously to follow that call.
            """,
        HowToFixIt: """
            Remove the SELECT/PRINT from the trigger body, or replace it with something that doesn't
            emit client-visible output - logging to a table, or raising an error via THROW/RAISERROR
            if the intent was actually to signal a problem to the caller.
            """,
        Examples:
        [
            new RuleDocExample(
                Title: "A trigger body with a real SELECT result set",
                NoncompliantSql: """
                    CREATE TRIGGER trg_Orders_AfterInsert ON dbo.Orders
                    AFTER INSERT
                    AS
                    BEGIN
                        SELECT * FROM inserted;
                    END;
                    """,
                NoncompliantExplanation: "This SELECT sends a full result set back to whatever connection issued the INSERT that fired this trigger - not to any application code expecting it, since the caller never asked for trigger output at all.",
                CompliantSql: """
                    CREATE TRIGGER trg_Orders_AfterInsert ON dbo.Orders
                    AFTER INSERT
                    AS
                    BEGIN
                        INSERT INTO dbo.OrderAuditLog (OrderId, LoggedAt)
                        SELECT Id, SYSUTCDATETIME() FROM inserted;
                    END;
                    """,
                CompliantExplanation: "The trigger now writes to an audit table instead of sending a result set back to the triggering connection - no client-visible output at all."),
        ]);
}
