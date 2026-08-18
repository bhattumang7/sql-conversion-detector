using SilentScan.Core.Reporting.Sarif;

namespace SilentScan.Core.Reporting.RuleDocs.Trigger;

internal static class MultiHopRecursionCycle
{
    public static string RuleId => SarifRuleCatalog.TriggerRecursionCycleRuleId;

    public static RuleDocContent Content { get; } = new(
        WhyItMatters: """
            This is the indirect counterpart to a trigger recursing directly into its own table: here
            the cycle spans two or more distinct tables, each with its own trigger, forming a
            directed loop rather than a single self-reference. Table A's trigger writes to table B;
            table B's own trigger, in turn, writes back toward table A (either directly, or through a
            further chain of tables C, D, and so on before eventually reaching back to A). No single
            trigger in the chain is recursive on its own - each one only writes to some other table -
            but the chain as a whole closes a loop.

            Whether this cycle is reachable at all depends on the server-level "nested triggers"
            option (sp_configure 'nested triggers'), which is ON by default and governs cross-table
            trigger chaining specifically - distinct from the database-level RECURSIVE_TRIGGERS
              option, which only governs a trigger firing directly on its own table. With nested
            triggers on (the common case), a write inside table A's trigger to table B genuinely
            fires table B's trigger, and if that trigger's own write eventually reaches back to table
            A, table A's trigger fires again - and if the cycle has no condition that stops matching
            rows on a subsequent pass, it keeps closing the loop.

            An uncontrolled cycle doesn't run forever: each hop through the chain is a nested trigger
            invocation, and SQL Server enforces a hard limit of 32 levels of nesting across stored
            procedures, functions, and triggers combined. The moment the cycle's depth crosses that
            limit, error 217 ("Maximum stored procedure, function, trigger, or view nesting level
            exceeded") aborts the statement that ultimately started the chain and rolls back its
            effects - a failure that can be several hops and several tables removed from wherever the
            application's original write actually happened, making it hard to trace back to its
            cause without already knowing to look for a cross-table trigger cycle.
            """,
        Examples:
        [
            new RuleDocExample(
                Title: "Table A's trigger writes to table B, whose trigger writes back to table A",
                NoncompliantSql: """
                    CREATE TABLE dbo.Orders  (OrderId INT NOT NULL PRIMARY KEY, Status VARCHAR(20) NOT NULL);
                    CREATE TABLE dbo.Shipments (ShipmentId INT NOT NULL PRIMARY KEY, OrderId INT NOT NULL, Status VARCHAR(20) NOT NULL);

                    CREATE TRIGGER dbo.trg_Orders_StatusPropagate ON dbo.Orders
                    AFTER UPDATE
                    AS
                    BEGIN
                        IF @@ROWCOUNT = 0 RETURN;

                        UPDATE s
                        SET s.Status = i.Status
                        FROM dbo.Shipments AS s
                        JOIN inserted AS i ON i.OrderId = s.OrderId;
                    END;
                    GO

                    CREATE TRIGGER dbo.trg_Shipments_StatusPropagate ON dbo.Shipments
                    AFTER UPDATE
                    AS
                    BEGIN
                        IF @@ROWCOUNT = 0 RETURN;

                        UPDATE o
                        SET o.Status = i.Status
                        FROM dbo.Orders AS o
                        JOIN inserted AS i ON i.OrderId = o.OrderId;
                    END;
                    """,
                NoncompliantExplanation: "An UPDATE on dbo.Orders fires trg_Orders_StatusPropagate, which writes to dbo.Shipments and fires trg_Shipments_StatusPropagate, which writes back to dbo.Orders and re-fires trg_Orders_StatusPropagate - with nested triggers on and no condition that eventually stops the Status values from differing, the cycle keeps closing until it crosses the 32-level nesting limit and error 217 aborts the original UPDATE."),
        ]);
}
