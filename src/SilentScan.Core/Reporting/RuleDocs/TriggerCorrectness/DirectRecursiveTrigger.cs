using SilentScan.Core.Reporting.Sarif;

namespace SilentScan.Core.Reporting.RuleDocs.TriggerCorrectness;

internal static class DirectRecursiveTrigger
{
    public static string RuleId => SarifRuleCatalog.TriggerCorrectnessDirectRecursiveTriggerRuleId;

    public static RuleDocContent Content { get; } = new(
        WhyItMatters: """
            SQL Server governs whether a trigger can re-fire itself via the database-level
            RECURSIVE_TRIGGERS option (sys.databases.is_recursive_triggers_on), checked separately
            from the server-level "nested triggers" setting that governs cross-table recursion.
            When RECURSIVE_TRIGGERS is OFF - the default in a newly created database - a trigger
            that writes back to its own target table inside its own body has that write's effect on
            the table happen, but the write does not re-invoke the trigger; SQL Server suppresses
            direct self-recursion specifically. Many authors write this pattern assuming that
            suppression is unconditional, or simply never think about it because it happened not to
            recurse in whatever environment they tested against.

            That assumption breaks the moment RECURSIVE_TRIGGERS is turned ON for the database -
            whether deliberately by another team, by a restore from an environment where it was
            already on, or by a database-level configuration script applied uniformly across
            several databases without auditing each trigger against it. With the option live, the
            trigger's own UPDATE/INSERT/DELETE against its own table does re-invoke the trigger, and
            the trigger runs again, potentially performing the same kind of write again, and so on.
            Whether this converges depends entirely on whether the trigger's own logic includes a
            base case that eventually stops matching rows - something rarely designed for
            deliberately, since the author wrote the trigger assuming recursion could never happen.

            When it doesn't converge, the recursion doesn't run forever: SQL Server enforces a hard
            nesting-level limit of 32, and the 33rd nested invocation raises error 217 ("Maximum
            stored procedure, function, trigger, or view nesting level exceeded"), aborting the
            triggering statement and rolling back its effects. This turns what looked like an
            ordinary write into a hard failure the instant the database-level setting changes, with
            no code change in the trigger itself required to cause it.
            """,
        HowToFixIt: """
            There are two independent, legitimate fixes, and which one is right depends on intent.
            If the recursive write was never intentional, turn RECURSIVE_TRIGGERS OFF at the
            database level (ALTER DATABASE ... SET RECURSIVE_TRIGGERS OFF) so the engine goes back
            to suppressing this trigger's self-invocation the way most authors assume it already
            does. If recursive self-invocation genuinely is the intended design, leave the option
            on but add an explicit recursion guard inside the trigger itself - most commonly a flag
            set via CONTEXT_INFO or SESSION_CONTEXT checked at trigger entry, so the trigger can
            detect it is already running on this connection and skip re-entering its own logic on
            the nested invocation.
            """,
        Examples:
        [
            new RuleDocExample(
                Title: "A trigger writes back to its own table while RECURSIVE_TRIGGERS is on",
                NoncompliantSql: """
                    ALTER DATABASE CURRENT SET RECURSIVE_TRIGGERS ON;

                    CREATE TABLE dbo.Employees
                    (
                        EmployeeId INT NOT NULL PRIMARY KEY,
                        ManagerId  INT NULL,
                        Level      INT NOT NULL
                    );

                    CREATE TRIGGER dbo.trg_Employees_Update ON dbo.Employees
                    AFTER UPDATE
                    AS
                    BEGIN
                        IF @@ROWCOUNT = 0 RETURN;

                        UPDATE e
                        SET e.Level = m.Level + 1
                        FROM dbo.Employees AS e
                        JOIN inserted AS i ON i.EmployeeId = e.EmployeeId
                        JOIN dbo.Employees AS m ON m.EmployeeId = i.ManagerId;
                    END;
                    """,
                NoncompliantExplanation: "With RECURSIVE_TRIGGERS ON, the trigger's own UPDATE against dbo.Employees re-fires trg_Employees_Update; if a chain of manager relationships keeps producing rows whose Level changes, the recursion continues until it hits SQL Server's 32-level nesting limit and error 217 aborts the whole statement.",
                CompliantSql: """
                    ALTER DATABASE CURRENT SET RECURSIVE_TRIGGERS OFF;
                    """,
                CompliantExplanation: "With RECURSIVE_TRIGGERS OFF, the trigger's own write to dbo.Employees still happens, but it no longer re-invokes trg_Employees_Update - the direct self-recursion is suppressed at the engine level."),
        ]);
}
