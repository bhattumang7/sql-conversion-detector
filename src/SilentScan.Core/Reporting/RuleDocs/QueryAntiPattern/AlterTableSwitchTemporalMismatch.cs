using SilentScan.Core.Predicates;
using SilentScan.Core.Reporting.Sarif;

namespace SilentScan.Core.Reporting.RuleDocs.QueryAntiPattern;

internal static class AlterTableSwitchTemporalMismatch
{
    public static string RuleId => SarifRuleCatalog.QueryAntiPatternRuleId(QueryAntiPatternFindingKind.AlterTableSwitchTemporalMismatch);

    public static RuleDocContent Content { get; } = new(
        WhyItMatters: """
            System-versioned temporal tables (WITH (SYSTEM_VERSIONING = ON)) track row history
            automatically via a PERIOD FOR SYSTEM_TIME - every UPDATE/DELETE moves the prior row
            version into a linked history table instead of just overwriting it. ALTER TABLE ...
            SWITCH is a metadata-only operation with no awareness of this machinery, so the engine
            requires the source and target tables to agree on whether that machinery exists at
            all: one carrying a SYSTEM_TIME PERIOD while the other doesn't makes the statement fail
            outright, independent of anything else about either table.

            This surfaces the same way the other SWITCH mismatches do: a staging table built
            without realizing the partitioned production table is system-versioned (or vice versa,
            after someone turns SYSTEM_VERSIONING on for the production table but doesn't revisit
            the staging table it's fed from).
            """,
        HowToFixIt: """
            Make the source and target tables agree on system-versioning: either both carry a
            PERIOD FOR SYSTEM_TIME (with SYSTEM_VERSIONING = ON), or neither does, before running
            the SWITCH.
            """,
        Examples:
        [
            new RuleDocExample(
                Title: "A non-versioned staging table switching into a system-versioned target",
                NoncompliantSql: """
                    CREATE TABLE dbo.Orders (
                        Id INT NOT NULL PRIMARY KEY,
                        SysStart DATETIME2 GENERATED ALWAYS AS ROW START NOT NULL,
                        SysEnd DATETIME2 GENERATED ALWAYS AS ROW END NOT NULL,
                        PERIOD FOR SYSTEM_TIME (SysStart, SysEnd)
                    ) WITH (SYSTEM_VERSIONING = ON (HISTORY_TABLE = dbo.OrdersHistory));
                    CREATE TABLE dbo.OrdersStaging (Id INT NOT NULL PRIMARY KEY);
                    -- (Orders is partitioned; OrdersStaging holds a batch to load in.)

                    ALTER TABLE dbo.OrdersStaging SWITCH TO dbo.Orders PARTITION 1;
                    -- Msg 13577: target table has SYSTEM_TIME PERIOD while source table does not
                    -- have it.
                    """,
                NoncompliantExplanation: "The target table is system-versioned and the source table isn't - the engine refuses the SWITCH outright with error 13577.",
                CompliantSql: """
                    CREATE TABLE dbo.OrdersStaging (
                        Id INT NOT NULL PRIMARY KEY,
                        SysStart DATETIME2 GENERATED ALWAYS AS ROW START NOT NULL,
                        SysEnd DATETIME2 GENERATED ALWAYS AS ROW END NOT NULL,
                        PERIOD FOR SYSTEM_TIME (SysStart, SysEnd)
                    ) WITH (SYSTEM_VERSIONING = ON (HISTORY_TABLE = dbo.OrdersStagingHistory));

                    ALTER TABLE dbo.OrdersStaging SWITCH TO dbo.Orders PARTITION 1;
                    """,
                CompliantExplanation: "Both tables are now system-versioned - the temporal-agreement check passes and the SWITCH proceeds to the engine's remaining checks."),
        ]);
}
