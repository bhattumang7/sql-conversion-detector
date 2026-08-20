using SilentScan.Core.Predicates;
using SilentScan.Core.Reporting.Sarif;

namespace SilentScan.Core.Reporting.RuleDocs.StatementShape;

internal static class MissingSetNocountOn
{
    public static string RuleId => SarifRuleCatalog.StatementShapeRuleId(StatementShapeFindingKind.MissingSetNocountOn);

    public static RuleDocContent Content { get; } = new(
        WhyItMatters: """
            A `CREATE`/`ALTER PROCEDURE` or `CREATE`/`ALTER TRIGGER` body with no `SET NOCOUNT ON`
            anywhere sends a client-visible "N rows affected" message after every single DML
            statement it executes - each one is its own extra round of protocol chatter between the
            server and the client. For a procedure that runs one statement, this is negligible; for
            a multi-statement routine - a loop, a batch of related INSERT/UPDATE/DELETE statements,
            a trigger firing on every row of a bulk operation - this is real, measurable network and
            processing cost that accumulates statement by statement, invisible today unless someone
            actually profiles for it.

            This applies with particular force to triggers, since a trigger's own rowcount messages
            are sent for every firing, and a trigger fires on every qualifying DML statement against
            its table - a busy table with an unset-NOCOUNT trigger pays this cost continuously,
            hidden inside ordinary application traffic that never looks like it's doing anything
            wrong.
            """,
        HowToFixIt: """
            Add SET NOCOUNT ON at the top of the procedure or trigger.
            """,
        Examples:
        [
            new RuleDocExample(
                Title: "A procedure with no SET NOCOUNT ON",
                NoncompliantSql: """
                    CREATE PROCEDURE dbo.usp_ArchiveOldOrders AS
                    BEGIN
                        DELETE FROM dbo.Orders WHERE OrderDate < DATEADD(YEAR, -2, GETDATE());
                        INSERT INTO dbo.OrderArchive SELECT * FROM dbo.OrdersStaging;
                    END;
                    """,
                NoncompliantExplanation: "Both the DELETE and the INSERT each send their own \"N rows affected\" message back to the client - real, avoidable protocol chatter on every call.",
                CompliantSql: """
                    CREATE PROCEDURE dbo.usp_ArchiveOldOrders AS
                    BEGIN
                        SET NOCOUNT ON;
                        DELETE FROM dbo.Orders WHERE OrderDate < DATEADD(YEAR, -2, GETDATE());
                        INSERT INTO dbo.OrderArchive SELECT * FROM dbo.OrdersStaging;
                    END;
                    """,
                CompliantExplanation: "SET NOCOUNT ON suppresses the rowcount message for every statement in the procedure's body, eliminating that extra round-trip cost."),
        ]);
}
