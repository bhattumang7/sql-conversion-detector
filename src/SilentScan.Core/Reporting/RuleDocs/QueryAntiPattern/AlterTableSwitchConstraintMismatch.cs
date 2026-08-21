using SilentScan.Core.Predicates;
using SilentScan.Core.Reporting.Sarif;

namespace SilentScan.Core.Reporting.RuleDocs.QueryAntiPattern;

internal static class AlterTableSwitchConstraintMismatch
{
    public static string RuleId => SarifRuleCatalog.QueryAntiPatternRuleId(QueryAntiPatternFindingKind.AlterTableSwitchConstraintMismatch);

    public static RuleDocContent Content { get; } = new(
        WhyItMatters: """
            ALTER TABLE ... SWITCH also requires the source and target tables to agree on CHECK
            and FOREIGN KEY constraints, for the same underlying reason column shape and indexes
            are checked: the swap moves storage without touching a single row, so nothing the
            target table's constraints promise can be allowed to silently stop being enforced the
            moment the data becomes the source table's data.

            The matching is by definition, not by name: a CHECK constraint on the source table
            counts as a match for one on the target table as long as its condition text is
            identical, even if the two constraints have completely different names (the same is
            true for a foreign key - it matches by referenced table and column, not by constraint
            name). For every constraint that exists on the target table, source needs an
            identically-defined counterpart that also agrees on enabled/disabled and NOCHECK/CHECK
            trust state. As with indexes, this only runs one direction - a constraint that exists
            only on the source table is never a problem, since it just switches in as extra
            enforcement - but a target constraint with nothing matching on the source blocks the
            whole statement.

            This typically surfaces the same way the column and index mismatches do: a staging
            table's constraints, once written and never revisited, drift out of sync with a
            partitioned production table's own evolving constraints - a CHECK added, a foreign key
            re-enabled after a data fix, a constraint's trust state changed by a maintenance
            script - and the SWITCH that used to work silently stops working.
            """,
        HowToFixIt: """
            Give the source table an identically-defined CHECK constraint (same condition text)
            and an identically-shaped FOREIGN KEY constraint (same referenced table and columns)
            for every constraint that exists on the target table, matching enabled/disabled and
            NOCHECK/CHECK trust state on both sides. The constraint names don't need to match -
            only the definition/shape and the state do.
            """,
        Examples:
        [
            new RuleDocExample(
                Title: "A staging table whose foreign key was left disabled after a data fix",
                NoncompliantSql: """
                    CREATE TABLE dbo.Regions (Id INT NOT NULL PRIMARY KEY);
                    CREATE TABLE dbo.OrdersStaging (Id INT NOT NULL, RegionId INT NOT NULL,
                        CONSTRAINT FK_OrdersStaging_Region FOREIGN KEY (RegionId) REFERENCES dbo.Regions(Id));
                    ALTER TABLE dbo.OrdersStaging NOCHECK CONSTRAINT FK_OrdersStaging_Region;
                    CREATE TABLE dbo.Orders (Id INT NOT NULL, RegionId INT NOT NULL,
                        CONSTRAINT FK_Orders_Region FOREIGN KEY (RegionId) REFERENCES dbo.Regions(Id));
                    -- (Orders is partitioned; OrdersStaging holds a batch to load in.)

                    ALTER TABLE dbo.OrdersStaging SWITCH TO dbo.Orders PARTITION 1;
                    -- Msg 4969: foreign key constraint 'FK_OrdersStaging_Region' in the source
                    -- table and matching constraint 'FK_Orders_Region' in the target table
                    -- disagree on enabled/disabled state.
                    """,
                NoncompliantExplanation: "The source table's matching foreign key (same referenced table and column, different name) was disabled during a one-off data fix and never re-enabled - the engine refuses the SWITCH outright with error 4969.",
                CompliantSql: """
                    ALTER TABLE dbo.OrdersStaging CHECK CONSTRAINT FK_OrdersStaging_Region;

                    ALTER TABLE dbo.OrdersStaging SWITCH TO dbo.Orders PARTITION 1;
                    """,
                CompliantExplanation: "Re-enabling the source table's foreign key restores agreement with the target table's matching constraint - the constraint-mismatch check passes and the SWITCH proceeds to the engine's remaining checks."),
        ]);
}
