using SilentScan.Core.Predicates;
using SilentScan.Core.Reporting.Sarif;

namespace SilentScan.Core.Reporting.RuleDocs.QueryAntiPattern;

internal static class RbarSingleRowLoopDml
{
    public static string RuleId => SarifRuleCatalog.QueryAntiPatternRuleId(QueryAntiPatternFindingKind.RbarSingleRowLoopDml);

    public static RuleDocContent Content { get; } = new(
        WhyItMatters: """
            SQL Server's engine is built around set-based execution: a single UPDATE or DELETE
            statement, given a predicate that matches many rows, plans one pass over an index or
            table and applies the change to every matching row in that one pass, sharing plan
            compilation, lock acquisition, log record generation, and trigger firing across the
            whole set. RBAR - row-by-agonizing-row - defeats every one of those efficiencies by
            wrapping the same statement shape in a WHILE loop that advances a key variable one row
            at a time and issues a fresh single-row UPDATE/DELETE keyed to that variable on every
            iteration.

            Each iteration pays its own fixed overhead independent of how small the actual row is:
            a new statement execution (even if the plan is cached and reused, there's still
            per-call overhead), its own lock acquisition and release, its own transaction log
            record, its own trigger firing if the table has one. None of that overhead exists in
            the set-based equivalent, where it's paid once for the whole batch rather than once per
            row. On a loop touching a few dozen rows this is invisible; on a loop touching tens of
            thousands the constant-per-row overhead dominates and the operation can be an order of
            magnitude slower than the equivalent single statement, purely from iteration overhead
            that does no useful work.

            This pattern usually arrives by two roads: code translated fairly directly from a
            procedural/imperative mental model (a for-loop in application code, ported into T-SQL
            as a WHILE loop), or a well-intentioned attempt to "chunk" a large operation that ends
            up chunking all the way down to a single row instead of a reasonably sized batch.
            """,
        HowToFixIt: """
            Replace the loop with a single UPDATE or DELETE whose WHERE clause expresses the same
            matching condition the loop was iterating over, so the engine applies the change to the
            whole matching set in one statement instead of one row per iteration. Where the
            original loop body's per-row logic genuinely differs row to row (not just the key it's
            keyed on), that logic usually still expresses as a CASE expression in the SET clause or
            as a join to a derived table carrying the per-row values, rather than requiring the
            loop to survive.
            """,
        Examples:
        [
            new RuleDocExample(
                Title: "A WHILE loop updating one row per iteration",
                NoncompliantSql: """
                    CREATE TABLE dbo.Invoices
                    (
                        InvoiceId INT          NOT NULL PRIMARY KEY,
                        Status    VARCHAR(20)  NOT NULL
                    );

                    DECLARE @InvoiceId INT;

                    DECLARE cur CURSOR LOCAL FAST_FORWARD FOR
                        SELECT InvoiceId FROM dbo.Invoices WHERE Status = 'Pending';

                    OPEN cur;
                    FETCH NEXT FROM cur INTO @InvoiceId;

                    WHILE @@FETCH_STATUS = 0
                    BEGIN
                        UPDATE dbo.Invoices
                        SET Status = 'Overdue'
                        WHERE InvoiceId = @InvoiceId;

                        FETCH NEXT FROM cur INTO @InvoiceId;
                    END;

                    CLOSE cur;
                    DEALLOCATE cur;
                    """,
                NoncompliantExplanation: "Every pending invoice gets its own UPDATE statement, its own lock acquisition, and its own log record, even though every iteration applies the exact same change (Status = 'Overdue') to a different row of the same set.",
                CompliantSql: """
                    UPDATE dbo.Invoices
                    SET Status = 'Overdue'
                    WHERE Status = 'Pending';
                    """,
                CompliantExplanation: "One statement applies the change to every matching row in a single pass over the table - no per-row iteration overhead, and the same net result."),
        ]);
}
