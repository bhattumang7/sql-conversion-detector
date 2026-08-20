using SilentScan.Core.Reporting.Sarif;

namespace SilentScan.Core.Reporting.RuleDocs.ControlFlow;

internal static class WaitFor
{
    public static string RuleId => SarifRuleCatalog.WaitForRuleId;

    public static RuleDocContent Content { get; } = new(
        WhyItMatters: """
            `WAITFOR DELAY`/`WAITFOR TIME` holds the calling worker thread idle for the entire
            delay, or until the specified time arrives - a documented, unconditional cost, not a
            plan-shape guess: the thread is genuinely blocked and cannot serve any other request for
            that duration. Under load, this contributes directly to worker-pool exhaustion - SQL
            Server has a finite scheduler thread pool, and every connection sitting inside a WAITFOR
            is a thread that isn't available to run anything else, including other sessions'
            queries.

            The finding also flags whether the WAITFOR sits inside an open transaction
            (`IsInsideTransaction`): a WAITFOR inside a transaction extends that transaction's lock
            hold duration for the exact same delay, so any locks already acquired stay held - and
              anything else blocked waiting on those locks stays blocked too - for the full wait,
            not just for genuine work.

            This is a purely syntax-only, fully general fact - no oracle needed, since a blocked
            worker thread is documented, unconditional engine behavior, not something that depends
            on data, statistics, or plan choice.
            """,
        Examples:
        [
            new RuleDocExample(
                Title: "WAITFOR DELAY inside an open transaction",
                NoncompliantSql: """
                    BEGIN TRANSACTION;
                    UPDATE dbo.Orders SET Status = 'Processing' WHERE Id = @OrderId;
                    WAITFOR DELAY '00:00:05';
                    COMMIT TRANSACTION;
                    """,
                NoncompliantExplanation: "The 5-second WAITFOR holds the worker thread idle AND extends this transaction's lock hold duration by the same 5 seconds - any other session blocked on the row this UPDATE locked stays blocked for the whole delay, not just for the UPDATE's own real work."),
        ]);
}
