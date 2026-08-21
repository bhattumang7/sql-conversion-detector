using SilentScan.Core.Predicates;
using SilentScan.Core.Reporting.Sarif;

namespace SilentScan.Core.Reporting.RuleDocs.QueryAntiPattern;

internal static class AlterTableSwitchIndexMismatch
{
    public static string RuleId => SarifRuleCatalog.QueryAntiPatternRuleId(QueryAntiPatternFindingKind.AlterTableSwitchIndexMismatch);

    public static RuleDocContent Content { get; } = new(
        WhyItMatters: """
            Beyond matching columns, ALTER TABLE ... SWITCH also requires the source and target
            tables to carry the same indexes. This isn't a performance nicety - it's a hard
            prerequisite the engine checks before the metadata-only swap is allowed at all, for
            the same reason column shape is checked: SWITCH reassigns storage without touching a
            single row, so both tables' physical structure (including every index built on top of
            it) has to already agree.

            Two separate requirements apply. First, clustered-index presence has to match exactly
            - if one table has a clustered index (rowstore) and the other doesn't, the SWITCH is
            refused outright, independent of anything else. Second, for every ordinary index that
            exists on the target table, the source table needs an "identical" counterpart: the
            same key columns in the same order, the same uniqueness, the same key sort direction
            (ASC/DESC), and the same INCLUDE column set. The comparison only runs one direction -
            an index on the source with no counterpart on the target is fine, since it just
            switches in as extra structure - but a target index with nothing matching on the
            source blocks the whole statement.

            As with column-shape drift, this typically surfaces when a staging table's indexes
            were built once and never kept in sync as the partitioned production table's own
            indexes evolved (a new covering index added, an INCLUDE column added for a query
            pattern, a sort direction changed) - the SWITCH that used to work silently stops
            working, usually only discovered when a scheduled load job fails.
            """,
        HowToFixIt: """
            Give the source table a clustered index if and only if the target table has one, and
            build an identical counterpart - same key columns and order, same uniqueness, same
            key sort direction, same INCLUDE columns - on the source table for every index that
            exists on the target table. Keeping the source table's index-creation script generated
            from (or reviewed against) the target table's own definition avoids the drift that
            causes this.
            """,
        Examples:
        [
            new RuleDocExample(
                Title: "A staging table missing an INCLUDE column the partitioned target's index has",
                NoncompliantSql: """
                    CREATE TABLE dbo.OrdersStaging (Id INT NOT NULL, Code VARCHAR(20) NOT NULL, Pct INT NOT NULL);
                    CREATE TABLE dbo.Orders (Id INT NOT NULL, Code VARCHAR(20) NOT NULL, Pct INT NOT NULL);
                    CREATE UNIQUE NONCLUSTERED INDEX IX_OrdersStaging_Code ON dbo.OrdersStaging(Code);
                    CREATE UNIQUE NONCLUSTERED INDEX IX_Orders_Code ON dbo.Orders(Code) INCLUDE (Pct);
                    -- (Orders is partitioned; OrdersStaging holds a batch to load in.)

                    ALTER TABLE dbo.OrdersStaging SWITCH TO dbo.Orders PARTITION 1;
                    -- Msg 4947: there is no identical index in source table 'OrdersStaging' for
                    -- the index 'IX_Orders_Code' in target table 'Orders'.
                    """,
                NoncompliantExplanation: "The target's index covers Pct via INCLUDE; the source's otherwise-matching index does not - the engine refuses the SWITCH outright with error 4947, before touching any row.",
                CompliantSql: """
                    CREATE UNIQUE NONCLUSTERED INDEX IX_OrdersStaging_Code ON dbo.OrdersStaging(Code) INCLUDE (Pct);

                    ALTER TABLE dbo.OrdersStaging SWITCH TO dbo.Orders PARTITION 1;
                    """,
                CompliantExplanation: "The source's index now carries the identical INCLUDE column set - the matching-index-set check passes and the SWITCH proceeds to the engine's remaining checks."),
        ]);
}
