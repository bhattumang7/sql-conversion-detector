using SilentScan.Core.Reporting.Sarif;

namespace SilentScan.Core.Reporting.RuleDocs.CrossModule;

internal static class InconsistentLockOrder
{
    public static string RuleId => SarifRuleCatalog.CrossModuleLockOrderRuleId;

    public static RuleDocContent Content { get; } = new(
        WhyItMatters: """
            SQL Server's lock manager grants exclusive locks to whichever session asks first and
            makes every other session wait; it has no global view of what order "makes sense" across
            different procedures, and it doesn't need one as long as every writer touching the same
            set of tables acquires locks on them in the same relative order. The problem this rule
            looks for is two top-level procedures whose own direct write order, inside an explicit
            transaction, disagrees on that relative order for the same two base tables - one
            procedure writes TableA then TableB, another writes TableB then TableA. Individually,
            each procedure is completely correct in isolation; the bug only exists in the
            combination of the two.

            The textbook deadlock interleaving follows directly from that disagreement. Session 1
            runs the first procedure and takes an exclusive lock on TableA; at nearly the same
            moment, session 2 runs the second procedure and takes an exclusive lock on TableB.
            Session 1 then tries to lock TableB and blocks, waiting behind session 2's lock; session
            2 then tries to lock TableA and blocks, waiting behind session 1's lock. Neither session
            can proceed, and neither lock will ever release, because releasing it requires completing
            the transaction that's now blocked waiting on the other session. SQL Server's deadlock
            monitor detects this cycle - by default checking every five seconds - picks one session
            as the deadlock victim, kills its transaction with error 1205, and rolls it back so the
            other session can proceed.

            This is a genuinely intermittent bug: it only manifests when both procedures happen to
            run concurrently against the same rows (or the same pages/table, depending on locking
            granularity) closely enough in time for the interleaving above to occur. Under light
            load, or when the two procedures' typical invocations rarely overlap, the same code can
            run for a long time without ever deadlocking - and then start doing so the moment traffic
            or a change in call pattern makes concurrent execution common. Nothing about either
            procedure's own logic is wrong; the disagreement is purely about the relative order two
            independently-correct procedures chose.
            """,
        HowToFixIt: """
            Make both procedures acquire locks on the two tables in the same relative order - pick
            one order (commonly whichever is more natural, or simply alphabetical/schema order as a
            project-wide convention) and rewrite whichever procedure disagrees with it so every
            writer touching both tables locks them in that same sequence. Once both procedures agree
            on order, the deadlock cycle described above becomes structurally impossible between
            them: one session's lock never sits between the other session's two acquisitions in a
            way that closes a wait-for cycle.
            """,
        Examples:
        [
            new RuleDocExample(
                Title: "Two procedures disagree on lock order between the same two tables",
                NoncompliantSql: """
                    CREATE TABLE dbo.Accounts (AccountId INT NOT NULL PRIMARY KEY, Balance DECIMAL(12,2) NOT NULL);
                    CREATE TABLE dbo.Ledger   (LedgerId  INT NOT NULL PRIMARY KEY, AccountId INT NOT NULL, Amount DECIMAL(12,2) NOT NULL);

                    CREATE PROCEDURE dbo.ApplyDeposit (@AccountId INT, @Amount DECIMAL(12,2), @LedgerId INT)
                    AS
                    BEGIN
                        BEGIN TRANSACTION;
                            UPDATE dbo.Accounts SET Balance = Balance + @Amount WHERE AccountId = @AccountId;
                            INSERT INTO dbo.Ledger (LedgerId, AccountId, Amount) VALUES (@LedgerId, @AccountId, @Amount);
                        COMMIT TRANSACTION;
                    END;
                    GO

                    CREATE PROCEDURE dbo.ReconcileLedgerEntry (@LedgerId INT, @AccountId INT, @Amount DECIMAL(12,2))
                    AS
                    BEGIN
                        BEGIN TRANSACTION;
                            UPDATE dbo.Ledger SET Amount = @Amount WHERE LedgerId = @LedgerId;
                            UPDATE dbo.Accounts SET Balance = Balance + @Amount WHERE AccountId = @AccountId;
                        COMMIT TRANSACTION;
                    END;
                    """,
                NoncompliantExplanation: "ApplyDeposit locks Accounts then Ledger, while ReconcileLedgerEntry locks Ledger then Accounts - when both run concurrently, session 1 can hold Accounts while waiting on Ledger at the same moment session 2 holds Ledger while waiting on Accounts, a deadlock cycle SQL Server resolves by killing one transaction with error 1205.",
                CompliantSql: """
                    CREATE PROCEDURE dbo.ReconcileLedgerEntry (@LedgerId INT, @AccountId INT, @Amount DECIMAL(12,2))
                    AS
                    BEGIN
                        BEGIN TRANSACTION;
                            UPDATE dbo.Accounts SET Balance = Balance + @Amount WHERE AccountId = @AccountId;
                            UPDATE dbo.Ledger SET Amount = @Amount WHERE LedgerId = @LedgerId;
                        COMMIT TRANSACTION;
                    END;
                    """,
                CompliantExplanation: "ReconcileLedgerEntry now locks Accounts before Ledger, the same order ApplyDeposit uses - with both procedures agreeing on order, the wait-for cycle that produced the deadlock can no longer form."),
        ]);
}
