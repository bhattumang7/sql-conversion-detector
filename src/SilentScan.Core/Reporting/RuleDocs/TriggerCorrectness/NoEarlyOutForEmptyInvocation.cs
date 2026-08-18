using SilentScan.Core.Reporting.Sarif;

namespace SilentScan.Core.Reporting.RuleDocs.TriggerCorrectness;

internal static class NoEarlyOutForEmptyInvocation
{
    public static string RuleId => SarifRuleCatalog.TriggerCorrectnessNoEarlyOutForEmptyInvocationRuleId;

    public static RuleDocContent Content { get; } = new(
        WhyItMatters: """
            A DML trigger fires once per triggering statement, but that doesn't mean inserted or
            deleted is guaranteed to hold any rows. An UPDATE or DELETE whose WHERE clause matches
            zero rows still counts as a statement that ran against the trigger's table, and SQL
            Server still fires AFTER triggers for it - inserted/deleted are simply empty in that
            invocation. The same is true for a MERGE whose matched/not-matched branches happen to
            touch zero rows for a particular clause, and for certain replication and
            change-data-capture-driven paths that can invoke a trigger against an empty row set as
            part of their own bookkeeping. None of this is exotic - "UPDATE ... WHERE some
            condition that matches nothing today" is an ordinary, common statement shape.

            A trigger body with no guard at its top runs its full logic regardless - every join
            against inserted/deleted, every subquery, every side-effecting write it contains -
            even when there is provably nothing to do. Joins against an empty pseudo-table still
            cost a plan compilation and execution; a trigger that writes to an audit table, calls
            other procedures, or does non-trivial computation pays that full cost on every empty
            invocation just as it would on a real one, silently multiplying overhead on any
            workload heavy with no-op UPDATE/DELETE statements.

            This is an advisory, not a correctness bug: a well-guarded trigger and an unguarded one
            both produce correct results on an empty invocation, since every operation against an
            empty inserted/deleted is naturally a no-op. The cost is purely wasted work - extra
            plan execution for logic that was never going to touch a row - which is why this is a
            widely followed convention rather than something the engine itself requires.
            """,
        HowToFixIt: """
            Add a cheap early-out guard at the very top of the trigger body, before any other logic
            runs: IF NOT EXISTS (SELECT 1 FROM inserted) AND NOT EXISTS (SELECT 1 FROM deleted)
            RETURN; (adjusted to whichever pseudo-table(s) the trigger actually reads), or
            equivalently IF @@ROWCOUNT = 0 RETURN; as the very first statement, before @@ROWCOUNT
            is reset by anything else. Either form lets the trigger skip its entire body on an
            empty invocation for the cost of one cheap existence check.
            """,
        Examples:
        [
            new RuleDocExample(
                Title: "A trigger with no guard runs its full body on a zero-row UPDATE",
                NoncompliantSql: """
                    CREATE TABLE dbo.Accounts
                    (
                        AccountId INT           NOT NULL PRIMARY KEY,
                        Balance   DECIMAL(12,2) NOT NULL,
                        Status    VARCHAR(20)   NOT NULL
                    );

                    CREATE TABLE dbo.AccountAudit
                    (
                        AccountId  INT           NOT NULL,
                        OldBalance DECIMAL(12,2) NOT NULL,
                        NewBalance DECIMAL(12,2) NOT NULL,
                        ChangedAt  DATETIME2     NOT NULL DEFAULT SYSUTCDATETIME()
                    );

                    CREATE TRIGGER dbo.trg_Accounts_Update ON dbo.Accounts
                    AFTER UPDATE
                    AS
                    BEGIN
                        INSERT INTO dbo.AccountAudit (AccountId, OldBalance, NewBalance)
                        SELECT i.AccountId, d.Balance, i.Balance
                        FROM inserted AS i
                        JOIN deleted AS d ON d.AccountId = i.AccountId;
                    END;
                    """,
                NoncompliantExplanation: "UPDATE dbo.Accounts SET Balance = Balance WHERE Status = 'Closed' still fires this trigger even when zero rows have Status = 'Closed' - the INSERT ... SELECT still compiles and executes a join against an empty inserted/deleted every time, for no result.",
                CompliantSql: """
                    CREATE TRIGGER dbo.trg_Accounts_Update ON dbo.Accounts
                    AFTER UPDATE
                    AS
                    BEGIN
                        IF @@ROWCOUNT = 0 RETURN;

                        INSERT INTO dbo.AccountAudit (AccountId, OldBalance, NewBalance)
                        SELECT i.AccountId, d.Balance, i.Balance
                        FROM inserted AS i
                        JOIN deleted AS d ON d.AccountId = i.AccountId;
                    END;
                    """,
                CompliantExplanation: "The @@ROWCOUNT check as the very first statement skips the audit join entirely on an empty invocation, at the cost of one cheap check instead of a full join execution."),
        ]);
}
