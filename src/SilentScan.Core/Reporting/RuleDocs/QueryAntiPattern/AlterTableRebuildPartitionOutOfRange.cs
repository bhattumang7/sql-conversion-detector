using SilentScan.Core.Predicates;
using SilentScan.Core.Reporting.Sarif;

namespace SilentScan.Core.Reporting.RuleDocs.QueryAntiPattern;

internal static class AlterTableRebuildPartitionOutOfRange
{
    public static string RuleId => SarifRuleCatalog.QueryAntiPatternRuleId(QueryAntiPatternFindingKind.AlterTableRebuildPartitionOutOfRange);

    public static RuleDocContent Content { get; } = new(
        WhyItMatters: """
            ALTER TABLE ... REBUILD PARTITION = n and ALTER INDEX ... REBUILD PARTITION = n both
            target one specific partition by its ordinal number. That number only means something
            in the context of the table's own partition scheme - a scheme built on a partition
            function with k boundary values always carves the table into exactly k + 1 partitions,
            no more, no fewer. A partition number that is inside the universal 1-15000 range but
            still higher than the table's own partition count does not silently target the last
            partition or round down - the engine rejects it outright, oracle-confirmed Msg 7730,
            "Alter index statement failed because partition number N does not exist in index
            '...'.", before any rebuild work happens.

            This is a purely compile-time fact once the target table's partition scheme is known
            from the catalog: the boundary value count fixes the partition count, and a literal
            partition number above it can never succeed, regardless of what data the table holds.
            """,
        HowToFixIt: """
            Reference only a partition number that actually exists on the table's partition scheme
            - count the scheme's partition function boundary values and add one, or query
            sys.partitions for the table to confirm the actual partition count before hard-coding
            a number. If the intent is to rebuild every partition, omit PARTITION = n and rebuild
            the whole table or index instead.
            """,
        Examples:
        [
            new RuleDocExample(
                Title: "REBUILD PARTITION references a partition number the scheme never created",
                NoncompliantSql: """
                    CREATE PARTITION FUNCTION PfSales (int) AS RANGE LEFT FOR VALUES (10, 20, 30);
                    CREATE PARTITION SCHEME PsSales AS PARTITION PfSales ALL TO ([PRIMARY]);
                    CREATE TABLE dbo.Sales (Id INT NOT NULL, Grp INT NOT NULL) ON PsSales(Grp);
                    CREATE CLUSTERED INDEX IX_Sales ON dbo.Sales(Grp, Id) ON PsSales(Grp);

                    ALTER TABLE dbo.Sales REBUILD PARTITION = 5;
                    """,
                NoncompliantExplanation: "PfSales has 3 boundary values, so dbo.Sales only has 4 partitions (1-4) - partition 5 does not exist, and the statement fails with Msg 7730 regardless of the table's data.",
                CompliantSql: """
                    ALTER TABLE dbo.Sales REBUILD PARTITION = 4;
                    """,
                CompliantExplanation: "Partition 4 is the last partition the scheme actually created, so the rebuild targets a real partition and succeeds."),
        ]);
}
