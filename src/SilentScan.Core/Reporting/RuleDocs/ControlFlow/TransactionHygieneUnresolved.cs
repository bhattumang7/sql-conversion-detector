using SilentScan.Core.Predicates;
using SilentScan.Core.Reporting.Sarif;

namespace SilentScan.Core.Reporting.RuleDocs.ControlFlow;

internal static class TransactionHygieneUnresolved
{
    public static string RuleId => SarifRuleCatalog.TransactionHygieneRuleId(TransactionHygieneFindingKind.UnresolvedOnSomePath);

    public static RuleDocContent Content { get; } = new(
        WhyItMatters: """
            A `BEGIN TRANSACTION` that reaches a `RETURN`/`THROW`, or the natural end of the module
            body, on some statically reachable path with no intervening `COMMIT`/`ROLLBACK` leaves
            that transaction genuinely open when the procedure returns - this is a real reachability
            walk over the procedure's own control flow, not a heuristic, so it only fires on paths
            the code can actually take.

            This is oracle-confirmed directly against a real engine: SQL Server itself raises Msg
            266 ("Transaction count after EXECUTE indicates a mismatching number of BEGIN and
            COMMIT statements") and leaves the calling session's `@@TRANCOUNT` elevated by one the
            instant such a procedure returns - the transaction's locks stay held indefinitely,
            blocking every other session that needs them, until whatever eventually calls
            COMMIT/ROLLBACK on the now-confused transaction nesting, or the connection is closed and
            the transaction rolls back by default. Every path through a procedure that opens a
            transaction needs a matching close on that same path - a single unclosed path is enough
            to leak a transaction on every call that happens to take it.

            A `ROLLBACK TRANSACTION` that names a savepoint does not close the transaction it
            partially undoes - oracle-confirmed `@@TRANCOUNT` is completely unaffected by a
            savepoint rollback, unlike a full `ROLLBACK TRANSACTION`. A `SAVE TRANSACTION
            sp1; ... ROLLBACK TRANSACTION sp1; RETURN;` path leaves the transaction just as open as
            a bare `BEGIN TRANSACTION; RETURN;` would.
            """,
        HowToFixIt: """
            Ensure every RETURN/THROW path, and the natural end of the module body, is preceded by a
            matching COMMIT or ROLLBACK for every BEGIN TRANSACTION - a ROLLBACK TRANSACTION naming
            a savepoint does not count as that matching close.
            """,
        Examples:
        [
            new RuleDocExample(
                Title: "A procedure that opens a transaction but never closes it",
                NoncompliantSql: """
                    CREATE PROCEDURE dbo.usp_ProcessOrder
                        @OrderId INT
                    AS
                    BEGIN
                        BEGIN TRANSACTION;
                        UPDATE dbo.Orders SET Status = 'Processing' WHERE Id = @OrderId;
                        SELECT @OrderId;
                    END;
                    """,
                NoncompliantExplanation: "The procedure body ends with no COMMIT or ROLLBACK - @@TRANCOUNT is left elevated by one for the calling session, and this UPDATE's locks stay held indefinitely after the procedure returns.",
                CompliantSql: """
                    CREATE PROCEDURE dbo.usp_ProcessOrder
                        @OrderId INT
                    AS
                    BEGIN
                        BEGIN TRANSACTION;
                        UPDATE dbo.Orders SET Status = 'Processing' WHERE Id = @OrderId;
                        COMMIT TRANSACTION;
                        SELECT @OrderId;
                    END;
                    """,
                CompliantExplanation: "The transaction is committed before the procedure returns on every path, so no locks are left held."),
            new RuleDocExample(
                Title: "A savepoint rollback mistaken for a real rollback",
                NoncompliantSql: """
                    CREATE PROCEDURE dbo.usp_ProcessOrder
                        @OrderId INT
                    AS
                    BEGIN
                        BEGIN TRANSACTION;
                        SAVE TRANSACTION BeforeUpdate;
                        UPDATE dbo.Orders SET Status = 'Processing' WHERE Id = @OrderId;
                        IF (@@ERROR <> 0)
                        BEGIN
                            ROLLBACK TRANSACTION BeforeUpdate;
                            RETURN;
                        END
                        COMMIT TRANSACTION;
                    END;
                    """,
                NoncompliantExplanation: "ROLLBACK TRANSACTION BeforeUpdate only undoes the UPDATE back to the savepoint - it does not decrement @@TRANCOUNT, so the RETURN on this path leaves the outer transaction from BEGIN TRANSACTION still open.",
                CompliantSql: """
                    CREATE PROCEDURE dbo.usp_ProcessOrder
                        @OrderId INT
                    AS
                    BEGIN
                        BEGIN TRANSACTION;
                        SAVE TRANSACTION BeforeUpdate;
                        UPDATE dbo.Orders SET Status = 'Processing' WHERE Id = @OrderId;
                        IF (@@ERROR <> 0)
                        BEGIN
                            ROLLBACK TRANSACTION;
                            RETURN;
                        END
                        COMMIT TRANSACTION;
                    END;
                    """,
                CompliantExplanation: "ROLLBACK TRANSACTION with no savepoint name fully closes the outer transaction, so @@TRANCOUNT is back to zero before the RETURN."),
        ]);
}
