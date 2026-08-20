using SilentScan.Core.Predicates;
using SilentScan.Core.Reporting.Sarif;

namespace SilentScan.Core.Reporting.RuleDocs.IndexDesign;

internal static class ColumnstoreIndexOnDmlTargetTable
{
    public static string RuleId => SarifRuleCatalog.IndexDesignRuleId(IndexDesignFindingKind.ColumnstoreIndexOnDmlTargetTable);

    public static RuleDocContent Content { get; } = new(
        WhyItMatters: """
            A table carrying a columnstore index (clustered or nonclustered) that is also a direct
            INSERT/UPDATE/DELETE/MERGE target elsewhere in the scanned code has a real locking-
            granularity mismatch with what most developers expect from transactional DML. Confirmed
            directly against a real engine: a single-row DELETE inside an explicit transaction
            against a table carrying a clustered columnstore index takes a real ROWGROUP-granularity
            lock, not the per-row lock an ordinary rowstore DELETE takes - so unrelated concurrent
            access to every OTHER row sharing that same rowgroup can genuinely block behind this
            one row's transaction.

            This is shipped as a structural risk flag only, never a proven-cost claim: whether
            contention actually occurs is workload-dependent (the concurrent access pattern,
            rowgroup size, whether the DML actually lands in a compressed rowgroup versus the
            deltastore) and structurally out of reach for a static pass to determine. The finding
            is catalog-decidable - the table carries a columnstore index AND is a direct DML target
            somewhere in the scanned code, never through a view or dynamic SQL this pass can't see
            inside - but the actual contention risk depends on real workload data this pass never
            claims to know.
            """,
        Examples:
        [
            new RuleDocExample(
                Title: "A columnstore-indexed table also targeted by transactional row-level DML",
                NoncompliantSql: """
                    CREATE TABLE dbo.SalesFact (OrderId INT NOT NULL, Amount DECIMAL(10,2) NOT NULL);
                    CREATE CLUSTERED COLUMNSTORE INDEX CCI_SalesFact ON dbo.SalesFact;

                    -- Elsewhere in the same codebase:
                    BEGIN TRANSACTION;
                    UPDATE dbo.SalesFact SET Amount = 0 WHERE OrderId = @OrderId;
                    COMMIT TRANSACTION;
                    """,
                NoncompliantExplanation: "This single-row UPDATE inside an explicit transaction takes a ROWGROUP-granularity lock, not a per-row one - unrelated concurrent access to every other row sharing that rowgroup can block behind this transaction, a real risk this codebase's columnstore-and-transactional-DML combination creates."),
        ]);
}
