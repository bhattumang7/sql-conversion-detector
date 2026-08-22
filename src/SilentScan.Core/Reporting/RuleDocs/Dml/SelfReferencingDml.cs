using SilentScan.Core.Reporting.Sarif;

namespace SilentScan.Core.Reporting.RuleDocs.Dml;

internal static class SelfReferencingDml
{
    public static string RuleId => SarifRuleCatalog.SelfReferencingDmlRuleId;

    public static RuleDocContent Content { get; } = new(
        WhyItMatters: """
            When a single statement both reads and writes the same table - a DELETE whose WHERE
            clause subqueries the table it's deleting from, an INSERT ... SELECT that reads from
            the very table it inserts into, an UPDATE ... FROM that joins the target table back to
            itself, or any of these reached indirectly through a view built over the same base
            table - SQL Server can no longer assume the read side sees a stable snapshot of rows
            that the write side hasn't touched yet. As the statement proceeds, rows it has already
            modified could, in principle, be revisited by its own read side, producing a row that's
            read once, written, then read again in its new state and written again - the
            Halloween-problem family of anomalies, so named because it was first characterized on a
            statement that kept giving already-raised employees another raise as it repeatedly
            re-encountered their updated rows.

            The engine's defense against this is architectural, not optional, and it costs real
            plan work: for a self-referencing INSERT or DELETE, the plan gets an Eager Spool that
            fully materializes the read side into a worktable before a single write happens, so the
            write side can never see its own in-flight changes. For a self-referencing UPDATE ...
            FROM or MERGE, the plan instead gets an extra Sort forcing the same full-materialize-
            before-write ordering. Both are pure overhead relative to an otherwise identical
            statement whose read side names a different table - there's nothing to defensively
            spool or sort when the rows being read can never be the rows being written, so that
            version of the plan skips the extra operator entirely. This is oracle-confirmed by
            comparing the two plans directly: same row counts, same indexes, same statement shape,
            differing only in whether the read side names the write target - and the self-
            referencing version consistently carries the extra spool or sort.

            The performance cost is easy to miss because nothing about it shows up in the source
            text - the statement reads like ordinary DML, and the extra plan work only becomes
            visible in an actual execution plan, not in the query itself. It also scales with the
            table, not with how much data logically needs re-checking: a self-join DELETE against a
            large table pays for materializing its full read side even when, semantically, no row
            was ever going to overlap between what's read and what's deleted.

            One real, oracle-confirmed exception: a statement whose own TOP row limiter is the
            literal integer 1 (not PERCENT, not a variable) guarantees at most one row can ever be
            touched, and across all four statement kinds the extra spool or sort disappears from the
            plan entirely - this rule does not fire on that shape.
            """,
        Examples:
        [
            new RuleDocExample(
                Title: "A DELETE whose subquery reads the table it deletes from",
                NoncompliantSql: """
                    CREATE TABLE dbo.Orders
                    (
                        OrderId    INT NOT NULL PRIMARY KEY,
                        CustomerId INT NOT NULL,
                        CreatedAt  DATETIME2(0) NOT NULL
                    );

                    DELETE o
                    FROM dbo.Orders AS o
                    WHERE o.CreatedAt < DATEADD(YEAR, -1, SYSDATETIME())
                      AND o.OrderId NOT IN (
                          SELECT TOP (1) o2.OrderId
                          FROM dbo.Orders AS o2
                          WHERE o2.CustomerId = o.CustomerId
                          ORDER BY o2.CreatedAt DESC
                      );
                    """,
                NoncompliantExplanation: "The correlated subquery reads dbo.Orders - the exact table the DELETE writes to - to find each customer's most recent order and keep it. SQL Server must materialize the read side (an Eager Spool) before deleting any row, so the subquery can never see rows this same statement has already removed."),
        ]);
}
