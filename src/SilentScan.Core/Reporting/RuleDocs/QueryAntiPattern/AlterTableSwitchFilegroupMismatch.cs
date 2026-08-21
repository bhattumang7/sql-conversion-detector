using SilentScan.Core.Predicates;
using SilentScan.Core.Reporting.Sarif;

namespace SilentScan.Core.Reporting.RuleDocs.QueryAntiPattern;

internal static class AlterTableSwitchFilegroupMismatch
{
    public static string RuleId => SarifRuleCatalog.QueryAntiPatternRuleId(QueryAntiPatternFindingKind.AlterTableSwitchFilegroupMismatch);

    public static RuleDocContent Content { get; } = new(
        WhyItMatters: """
            ALTER TABLE ... SWITCH is a metadata-only operation - it reassigns which table's
            metadata points at a set of data pages, without moving those pages anywhere. That only
            works if the source and target tables' storage already lives in the same place: the
            same filegroup. If they don't, the engine refuses the statement outright rather than
            silently move gigabytes of data to make the SWITCH "work".

            A second, related restriction: a table sitting in a filegroup that's currently marked
            READ_ONLY can never be a SWITCH source or target, regardless of whether the other
            table's filegroup matches. This specifically catches a table that was created back
            when its filegroup was still read-write, and only later had READ_ONLY turned on
            (a table can never be created directly into an already-read-only filegroup, so this
            can only happen to a table that predates the filegroup's read-only flag) - a scheduled
            load job that used to SWITCH successfully can start failing the moment someone marks
            the filegroup read-only for an unrelated reason (e.g. to freeze old partitions for
            backup/archival purposes).
            """,
        HowToFixIt: """
            Put the source and target tables in the same, currently read-write filegroup before
            running the SWITCH. If a filegroup was intentionally marked read-only, either exclude
            its tables from the SWITCH workflow, or temporarily switch the filegroup back to
            read-write for the duration of the maintenance window.
            """,
        Examples:
        [
            new RuleDocExample(
                Title: "A staging table left in the default filegroup while its target moved to a dedicated one",
                NoncompliantSql: """
                    CREATE TABLE dbo.OrdersStaging (Id INT NOT NULL) ON [PRIMARY];
                    CREATE TABLE dbo.Orders (Id INT NOT NULL) ON FG_Orders;
                    -- (Orders is partitioned; OrdersStaging holds a batch to load in.)

                    ALTER TABLE dbo.OrdersStaging SWITCH TO dbo.Orders PARTITION 1;
                    -- Msg 4940: table 'OrdersStaging' is in filegroup 'PRIMARY' and table 'Orders'
                    -- is in filegroup 'FG_Orders'.
                    """,
                NoncompliantExplanation: "The two tables live in different filegroups - the engine refuses the SWITCH outright with error 4940, since the operation can't move data between filegroups.",
                CompliantSql: """
                    CREATE TABLE dbo.OrdersStaging (Id INT NOT NULL) ON FG_Orders;
                    CREATE TABLE dbo.Orders (Id INT NOT NULL) ON FG_Orders;

                    ALTER TABLE dbo.OrdersStaging SWITCH TO dbo.Orders PARTITION 1;
                    """,
                CompliantExplanation: "Both tables now live in the same filegroup - the filegroup check passes and the SWITCH proceeds to the engine's remaining checks."),
        ]);
}
