using SilentScan.Core.Predicates;
using SilentScan.Core.Reporting.Sarif;

namespace SilentScan.Core.Reporting.RuleDocs.IndexDesign;

internal static class RowOrPageLockingDisabled
{
    public static string RuleId => SarifRuleCatalog.IndexDesignRuleId(IndexDesignFindingKind.RowOrPageLockingDisabled);

    public static RuleDocContent Content { get; } = new(
        WhyItMatters: """
            `ALLOW_ROW_LOCKS` and `ALLOW_PAGE_LOCKS` are per-index options
            (`sys.indexes.allow_row_locks` / `allow_page_locks`), set at `CREATE INDEX`/inline
            constraint time or later via `ALTER INDEX ... SET (...)`. Both default to `ON`. Turning
            either `OFF` changes the locking granularity the engine is willing to take for any DML
            that touches that index, permanently, until someone turns it back on - a plain
            `UPDATE`/`DELETE`/`INSERT` statement gives no hint of this at its own call site, because
            the option lives entirely in the index's own catalog metadata.

            This is the same "hint/index-option silently reverting locking granularity in a way
            invisible from the DML site" shape as `READCOMMITTEDLOCK` reverting RCSI row versioning
            - except here there's no keyword anywhere in the DML text at all. Two statements a
            developer assumes can run concurrently against unrelated rows can end up blocking or
            deadlocking behind a coarser lock instead. Shipped as a structural risk flag only:
            whether contention actually occurs depends on the concurrent access pattern, which is
            workload data out of reach for a static pass.
            """,
        HowToFixIt: """
            Re-enable ALLOW_ROW_LOCKS and ALLOW_PAGE_LOCKS on the index unless the coarser locking
            granularity was a deliberate, documented choice.
            """,
        Examples:
        [
            new RuleDocExample(
                Title: "An index built with row-level locking disabled",
                NoncompliantSql: """
                    CREATE INDEX IX_Orders_CustomerId
                        ON dbo.Orders (CustomerId)
                        WITH (ALLOW_ROW_LOCKS = OFF);
                    """,
                NoncompliantExplanation: "Every UPDATE/DELETE that touches this index is forced onto a coarser locking granularity than row-level - invisible from the DML statement itself, which never mentions this index's own locking configuration.",
                CompliantSql: """
                    CREATE INDEX IX_Orders_CustomerId
                        ON dbo.Orders (CustomerId);
                    """,
                CompliantExplanation: "With ALLOW_ROW_LOCKS left at its default ON, the engine can take ordinary row-level locks for DML against this index."),
        ]);
}
