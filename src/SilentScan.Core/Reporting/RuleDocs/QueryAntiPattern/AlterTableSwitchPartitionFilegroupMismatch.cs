using SilentScan.Core.Predicates;
using SilentScan.Core.Reporting.Sarif;

namespace SilentScan.Core.Reporting.RuleDocs.QueryAntiPattern;

internal static class AlterTableSwitchPartitionFilegroupMismatch
{
    public static string RuleId => SarifRuleCatalog.QueryAntiPatternRuleId(QueryAntiPatternFindingKind.AlterTableSwitchPartitionFilegroupMismatch);

    public static RuleDocContent Content { get; } = new(
        WhyItMatters: """
            The whole-table filegroup check (see the sibling "filegroup mismatch" rule) only
            applies when neither side of the SWITCH names a specific partition number. Once a
            partition number is involved, the engine checks something more granular: the exact
            filegroup that SPECIFIC partition's data lives in, which a partition scheme can assign
            independently per partition (partition 1 in one filegroup, partition 2 in another, and
            so on).

            Two partitioned tables can easily end up with the same partition COUNT and boundary
            values but different underlying partition SCHEMES - one built with partitions cycling
            through filegroups A, B, A and another through B, A, B, for instance - so that
            partition 1 lands in a different filegroup on each side even though the tables look
            identically shaped from the boundary values alone. The same mismatch can also happen
            between a non-partitioned table and one specific partition of a partitioned table.
            """,
        HowToFixIt: """
            Make sure the specific partition (or, for a non-partitioned side, the whole table)
            named on each side of the SWITCH resolves to the same filegroup - either by using
            matching partition schemes on both tables, or by explicitly placing the relevant
            partition/table in the right filegroup.
            """,
        Examples:
        [
            new RuleDocExample(
                Title: "Two partition schemes on the same boundaries but cycling through filegroups in a different order",
                NoncompliantSql: """
                    CREATE PARTITION SCHEME PS_A AS PARTITION PF_Orders TO (FG_A, FG_B, FG_A);
                    CREATE PARTITION SCHEME PS_B AS PARTITION PF_Orders TO (FG_B, FG_A, FG_B);
                    CREATE TABLE dbo.OrdersStaging (Id INT NOT NULL) ON PS_B(Id);
                    CREATE TABLE dbo.Orders (Id INT NOT NULL) ON PS_A(Id);

                    ALTER TABLE dbo.OrdersStaging SWITCH PARTITION 1 TO dbo.Orders PARTITION 1;
                    -- Msg 4938: Partition 1 of table 'OrdersStaging' is in filegroup 'FG_B' and
                    -- partition 1 of table 'Orders' is in filegroup 'FG_A'.
                    """,
                NoncompliantExplanation: "Partition 1 of the source table lives in FG_B while partition 1 of the target table lives in FG_A, even though both schemes share the same boundary values - the engine refuses the SWITCH outright with error 4938.",
                CompliantSql: """
                    CREATE TABLE dbo.OrdersStaging (Id INT NOT NULL) ON PS_A(Id);

                    ALTER TABLE dbo.OrdersStaging SWITCH PARTITION 1 TO dbo.Orders PARTITION 1;
                    """,
                CompliantExplanation: "Both tables now use the same partition scheme, so partition 1 resolves to the same filegroup on both sides - the check passes and the SWITCH proceeds to the engine's remaining checks."),
        ]);
}
