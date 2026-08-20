using SilentScan.Core.Predicates;
using SilentScan.Core.Reporting.Sarif;

namespace SilentScan.Core.Reporting.RuleDocs.Database;

internal static class TargetRecoveryTimeUnset
{
    public static string RuleId => SarifRuleCatalog.DatabaseConfigurationRuleId(DatabaseConfigurationFindingKind.TargetRecoveryTimeUnset);

    public static RuleDocContent Content { get; } = new(
        WhyItMatters: """
            `TARGET_RECOVERY_TIME` at 0 disables indirect checkpoint entirely, falling the database
            back to the legacy automatic-checkpoint mechanism - one sized by the older `RECOVERY
            INTERVAL` server setting rather than a bounded, predictable crash-recovery time. This
            tool confirmed directly against a freshly created database on the same engine instance
            (the `model` system database every new database is cloned from) that the modern
            out-of-the-box default is `target_recovery_time_in_seconds = 60`, not 0 - a database
            sitting at 0 has deviated from that default. This is a specific, well-documented
            recommendation (Microsoft's own "Database Checkpoints" guidance, current since SQL
            Server 2016) to enable indirect checkpoint, not a workload judgment call.

            This is a database-level configuration fact, read once per scan directly from
            `sys.databases.target_recovery_time_in_seconds` - only available when scanning a live,
            connected target, since there is no file-mode equivalent of "the database's own current
            configuration."
            """,
        HowToFixIt: """
            Set TARGET_RECOVERY_TIME explicitly instead of leaving it at 0 (disabled).
            """,
        Examples:
        [
            new RuleDocExample(
                Title: "A database left at the legacy TARGET_RECOVERY_TIME of 0",
                NoncompliantSql: """
                    ALTER DATABASE CURRENT SET TARGET_RECOVERY_TIME = 0 SECONDS;
                    """,
                NoncompliantExplanation: "0 disables indirect checkpoint entirely, falling back to the legacy RECOVERY INTERVAL-sized automatic checkpoint instead of a bounded, predictable crash-recovery time - a deviation from the engine's own modern default of 60 seconds.",
                CompliantSql: """
                    ALTER DATABASE CURRENT SET TARGET_RECOVERY_TIME = 60 SECONDS;
                    """,
                CompliantExplanation: "Matching the engine's own modern default restores indirect checkpoint's bounded, predictable crash-recovery time."),
        ]);
}
