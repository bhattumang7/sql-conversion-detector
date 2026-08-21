using SilentScan.Core.Predicates;
using SilentScan.Core.Reporting.Sarif;

namespace SilentScan.Core.Reporting.RuleDocs.QueryAntiPattern;

internal static class AlterTableSwitchCdcPartitionSwitch
{
    public static string RuleId => SarifRuleCatalog.QueryAntiPatternRuleId(QueryAntiPatternFindingKind.AlterTableSwitchCdcPartitionSwitch);

    public static RuleDocContent Content { get; } = new(
        WhyItMatters: """
            Change Data Capture tracks row-level changes by reading the transaction log - a
            partition SWITCH moves rows between tables without generating the per-row log records
            CDC normally captures, so by default the engine just warns ("changes introduced by a
            partition switch will not be tracked") and lets the SWITCH proceed anyway, accepting
            the resulting gap in the CDC change stream.

            @allow_partition_switch is the opt-out: setting it to 0 when enabling CDC on a table
            (sp_cdc_enable_table's own parameter) turns that warning into a hard block - the SWITCH
            fails outright instead of silently creating an untracked gap. This is exactly backwards
            from what the setting's name suggests at a glance: 0 does not mean "switching is
            disabled as an operation" in some passive sense, it means "SWITCH statements against
            this table will now fail with a real error." A table's CDC configuration is easy to
            forget about when writing a routine partition-maintenance script months or years later.
            """,
        HowToFixIt: """
            If the CDC gap a partition switch creates is acceptable for this table, re-enable CDC
            with @allow_partition_switch = 1 (sp_cdc_disable_table, then sp_cdc_enable_table again
            with the parameter set explicitly). If it isn't acceptable, the SWITCH itself needs to
            be replaced with a row-by-row move that CDC can actually track, or CDC needs to be
            paused/reworked around the maintenance window some other way.
            """,
        Examples:
        [
            new RuleDocExample(
                Title: "A partitioned target table CDC-enabled with partition switching explicitly disallowed",
                NoncompliantSql: """
                    EXEC sys.sp_cdc_enable_table
                        @source_schema = N'dbo', @source_name = N'Orders',
                        @role_name = NULL, @allow_partition_switch = 0;

                    ALTER TABLE dbo.OrdersStaging SWITCH PARTITION 1 TO dbo.Orders PARTITION 1;
                    -- Msg 22842: ALTER TABLE SWITCH statement failed because the partitioned
                    -- destination table is enabled for Change Data Capture and does not have
                    -- @allow_partition_switch set to 1.
                    """,
                NoncompliantExplanation: "The target table's CDC capture instance explicitly disallows partition switching - the engine refuses the SWITCH outright with error 22842, instead of the usual warn-and-proceed default.",
                CompliantSql: """
                    EXEC sys.sp_cdc_disable_table @source_schema = N'dbo', @source_name = N'Orders', @capture_instance = N'all';
                    EXEC sys.sp_cdc_enable_table
                        @source_schema = N'dbo', @source_name = N'Orders',
                        @role_name = NULL, @allow_partition_switch = 1;

                    ALTER TABLE dbo.OrdersStaging SWITCH PARTITION 1 TO dbo.Orders PARTITION 1;
                    """,
                CompliantExplanation: "With @allow_partition_switch restored to 1 (the default), the SWITCH proceeds - the engine warns that changes from the switch won't be tracked by CDC, but no longer blocks the statement."),
        ]);
}
