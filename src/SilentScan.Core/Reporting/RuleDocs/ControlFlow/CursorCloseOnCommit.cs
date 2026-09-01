using SilentScan.Core.Reporting.Sarif;

namespace SilentScan.Core.Reporting.RuleDocs.ControlFlow;

internal static class CursorCloseOnCommit
{
    public static string RuleId => SarifRuleCatalog.CursorCloseOnCommitRuleId;

    public static RuleDocContent Content { get; } = new(
        WhyItMatters: """
            `SET CURSOR_CLOSE_ON_COMMIT ON` changes what a `COMMIT`/`ROLLBACK` does to every
            currently open cursor in the session: the instant the outermost transaction actually
            commits (or a full `ROLLBACK` runs, not one naming a savepoint), the engine silently
            closes every open cursor - oracle-confirmed (Msg 16917, "Cursor is not open") on the
            very next `FETCH`.

            The failure surfaces at the `FETCH` site, not at the `OPEN` site or the `COMMIT`/
            `ROLLBACK` site that actually caused it, so nothing in the source text at the point of
            failure hints at the real cause. A nested `BEGIN TRANSACTION`/`COMMIT` pair that only
            reduces `@@TRANCOUNT` without fully closing the outermost transaction does not trigger
            this - the same nesting-depth rule the engine itself applies. Re-opening the cursor
            after the `COMMIT`/`ROLLBACK` before the next `FETCH` avoids the failure entirely.

            This is a purely syntax-only fact once `CURSOR_CLOSE_ON_COMMIT` is known to be `ON` in
            the script/module's own text: a local, name-declared cursor's open/closed state across
            a `COMMIT`/`ROLLBACK` boundary requires no catalog access at all.
            """,
        Examples:
        [
            new RuleDocExample(
                Title: "FETCH after a mid-flow COMMIT with CURSOR_CLOSE_ON_COMMIT ON",
                NoncompliantSql: """
                    SET CURSOR_CLOSE_ON_COMMIT ON;
                    DECLARE cur CURSOR FOR SELECT Id FROM dbo.Orders;
                    OPEN cur;
                    FETCH NEXT FROM cur INTO @id;
                    COMMIT TRANSACTION;
                    FETCH NEXT FROM cur INTO @id;
                    """,
                NoncompliantExplanation: "The COMMIT silently closes cur - the second FETCH fails at runtime with Msg 16917 (\"Cursor is not open\"), with nothing at the FETCH site itself hinting that CURSOR_CLOSE_ON_COMMIT is the cause."),
        ]);
}
