using SilentScan.Core.Predicates;
using SilentScan.Core.Reporting.Sarif;

namespace SilentScan.Core.Reporting.RuleDocs.ControlFlow;

internal static class TransactionHygieneImplicit
{
    public static string RuleId => SarifRuleCatalog.TransactionHygieneRuleId(TransactionHygieneFindingKind.ImplicitTransactionUnresolvedOnSomePath);

    public static RuleDocContent Content { get; } = new(
        WhyItMatters: """
            `SET IMPLICIT_TRANSACTIONS ON` changes what opens a transaction - once it's on, the next
            `INSERT`/`UPDATE`/`DELETE`/`MERGE`/`TRUNCATE TABLE`, a `SELECT` with a `FROM` clause, a
            `CREATE`/`ALTER TABLE`/`DROP`, a `GRANT`/`REVOKE`, or an `OPEN`/`FETCH` against a cursor
            silently starts a transaction with no `BEGIN TRANSACTION` anywhere in the source text.
            This is oracle-confirmed directly: each of those statement kinds bumps `@@TRANCOUNT`
            from 0 to 1 under this setting with no explicit `BEGIN TRANSACTION` at all, and a bare
            `SELECT` with no `FROM` clause is oracle-confirmed to never do this.

            The same reachable-path analysis this tool already applies to an explicit `BEGIN
            TRANSACTION` applies here too: a `RETURN`/`THROW`, or the natural end of the module
            body, reached with no intervening `COMMIT`/`ROLLBACK` after one of these statements ran
            leaves `@@TRANCOUNT` elevated by one, holding locks exactly as an unresolved explicit
            transaction would - except there's no `BEGIN TRANSACTION` in the code to draw the eye to
            it.
            """,
        HowToFixIt: """
            Ensure every RETURN/THROW path, and the natural end of the module body, is preceded by a
            matching COMMIT or ROLLBACK once SET IMPLICIT_TRANSACTIONS ON is in effect - or turn the
            setting back off if leaving a transaction implicitly open wasn't intended.
            """,
        Examples:
        [
            new RuleDocExample(
                Title: "An implicitly opened transaction that is never closed",
                NoncompliantSql: """
                    CREATE PROCEDURE dbo.usp_ProcessOrder
                        @OrderId INT
                    AS
                    BEGIN
                        SET IMPLICIT_TRANSACTIONS ON;
                        UPDATE dbo.Orders SET Status = 'Processing' WHERE Id = @OrderId;
                        SELECT @OrderId;
                    END;
                    """,
                NoncompliantExplanation: "SET IMPLICIT_TRANSACTIONS ON turns the UPDATE into the start of a transaction with no BEGIN TRANSACTION written anywhere - the procedure body ends with no COMMIT or ROLLBACK, leaving @@TRANCOUNT elevated by one for the calling session.",
                CompliantSql: """
                    CREATE PROCEDURE dbo.usp_ProcessOrder
                        @OrderId INT
                    AS
                    BEGIN
                        SET IMPLICIT_TRANSACTIONS ON;
                        UPDATE dbo.Orders SET Status = 'Processing' WHERE Id = @OrderId;
                        COMMIT TRANSACTION;
                        SELECT @OrderId;
                    END;
                    """,
                CompliantExplanation: "The implicitly opened transaction is committed before the procedure returns on every path, so no locks are left held."),
        ]);
}
