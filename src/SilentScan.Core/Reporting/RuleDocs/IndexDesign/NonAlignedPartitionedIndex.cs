using SilentScan.Core.Predicates;
using SilentScan.Core.Reporting.Sarif;

namespace SilentScan.Core.Reporting.RuleDocs.IndexDesign;

internal static class NonAlignedPartitionedIndex
{
    public static string RuleId => SarifRuleCatalog.IndexDesignRuleId(IndexDesignFindingKind.NonAlignedPartitionedIndex);

    public static RuleDocContent Content { get; } = new(
        WhyItMatters: """
            The table's own clustered index is genuinely partitioned, but another active index on
            the same table isn't aligned with it - standard SQL Server terminology for a real,
            catalog-visible mismatch, confirmed directly against a real engine: either the index
            sits on a single, unpartitioned filegroup while the table itself is partitioned, or the
            index shares the identical partition scheme object as the table but is keyed on a
            different column, so its own partitions don't line up with the table's.

            Both shapes are real problems, not just naming inconsistencies: a non-aligned index
            cannot participate in a partition `SWITCH` against the table at all, and per-partition
            maintenance (rebuilding or reorganizing one partition) degrades to a full-index
            operation for the non-aligned index specifically, since the engine has no per-partition
            boundary to act on for it.

            Scoped to the table's own CLUSTERED, non-columnstore index as the alignment reference
            only - a partitioned heap (no clustered index at all) is out of scope, never guessed
            at.
            """,
        HowToFixIt: """
            Rebuild the index on the table's own partition scheme, keyed on the table's own
            partitioning column.
            """,
        Examples:
        [
            new RuleDocExample(
                Title: "A nonclustered index left on a single filegroup while its table is partitioned",
                NoncompliantSql: """
                    CREATE PARTITION FUNCTION PfOrderDate (date) AS RANGE RIGHT FOR VALUES ('2025-01-01', '2026-01-01');
                    CREATE PARTITION SCHEME PsOrderDate AS PARTITION PfOrderDate ALL TO ([PRIMARY]);

                    CREATE TABLE dbo.Orders
                    (
                        OrderId   int NOT NULL,
                        OrderDate date NOT NULL,
                        Region    int NOT NULL,
                        CONSTRAINT PK_Orders PRIMARY KEY CLUSTERED (OrderDate, OrderId)
                    ) ON PsOrderDate(OrderDate);

                    CREATE NONCLUSTERED INDEX IX_Orders_Region ON dbo.Orders(Region);
                    """,
                NoncompliantExplanation: "IX_Orders_Region has no ON clause, so it lands on the default [PRIMARY] filegroup - a single, unpartitioned structure sitting on top of a partitioned table. It cannot switch with the table's own partitions, and rebuilding it always rebuilds the whole index.",
                CompliantSql: """
                    CREATE NONCLUSTERED INDEX IX_Orders_Region ON dbo.Orders(Region) ON PsOrderDate(OrderDate);
                    """,
                CompliantExplanation: "Building the index on the table's own partition scheme, keyed on the table's own partitioning column (OrderDate), keeps every partition's own nonclustered index physically aligned with the matching data partition."),
        ]);
}
