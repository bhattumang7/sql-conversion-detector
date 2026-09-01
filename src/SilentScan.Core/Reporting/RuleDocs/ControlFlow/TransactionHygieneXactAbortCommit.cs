using SilentScan.Core.Predicates;
using SilentScan.Core.Reporting.Sarif;

namespace SilentScan.Core.Reporting.RuleDocs.ControlFlow;

internal static class TransactionHygieneXactAbortCommit
{
    public static string RuleId => SarifRuleCatalog.TransactionHygieneRuleId(TransactionHygieneFindingKind.CommitAfterXactAbortDoomsTransaction);

    public static RuleDocContent Content { get; } = new(
        WhyItMatters: """
            `SET XACT_ABORT ON` changes what a `CATCH` block can safely do with an already-open
            transaction. Oracle-confirmed directly: with `XACT_ABORT ON`, a transaction that was
            already open before a `TRY` block began is marked uncommittable - `XACT_STATE()` reads
            `-1` - the instant any error inside that `TRY` is caught, while `@@TRANCOUNT` itself
            stays unchanged (still 1). A `COMMIT TRANSACTION` reached directly inside the matching
            `CATCH` block then always fails with Msg 3930 ("The current transaction cannot be
            committed and cannot support operations that write to the log file. Roll back the
            transaction."), regardless of which error reached the CATCH block or what the COMMIT's
            own code otherwise does.

            This is a genuine blind spot for a `CATCH` block that looks correctly handled from
            source alone: `IF @@TRANCOUNT > 0 COMMIT TRANSACTION` reads as a defensive guard, but
            `@@TRANCOUNT` does not reflect the doomed state at all, so the guard never prevents the
            COMMIT from running and failing.
            """,
        HowToFixIt: """
            Replace the COMMIT TRANSACTION in the CATCH block with a ROLLBACK TRANSACTION - a
            transaction doomed by XACT_ABORT ON can only be rolled back, never committed.
            """,
        Examples:
        [
            new RuleDocExample(
                Title: "A CATCH block that tries to commit a doomed transaction",
                NoncompliantSql: """
                    CREATE PROCEDURE dbo.usp_ProcessOrder
                        @OrderId INT
                    AS
                    BEGIN
                        SET XACT_ABORT ON;
                        BEGIN TRANSACTION;
                        BEGIN TRY
                            UPDATE dbo.Orders SET Status = 'Processing' WHERE Id = @OrderId;
                            COMMIT TRANSACTION;
                        END TRY
                        BEGIN CATCH
                            COMMIT TRANSACTION;
                        END CATCH
                    END;
                    """,
                NoncompliantExplanation: "SET XACT_ABORT ON dooms the transaction the instant the UPDATE's error is caught - the CATCH block's own COMMIT TRANSACTION always fails with Msg 3930, it can never actually commit anything here.",
                CompliantSql: """
                    CREATE PROCEDURE dbo.usp_ProcessOrder
                        @OrderId INT
                    AS
                    BEGIN
                        SET XACT_ABORT ON;
                        BEGIN TRANSACTION;
                        BEGIN TRY
                            UPDATE dbo.Orders SET Status = 'Processing' WHERE Id = @OrderId;
                            COMMIT TRANSACTION;
                        END TRY
                        BEGIN CATCH
                            ROLLBACK TRANSACTION;
                        END CATCH
                    END;
                    """,
                CompliantExplanation: "ROLLBACK TRANSACTION is the only operation a doomed transaction accepts - the CATCH block now does the one thing that actually succeeds."),
        ]);
}
