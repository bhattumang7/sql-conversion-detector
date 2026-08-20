using SilentScan.Core.Predicates;
using SilentScan.Core.Reporting.Sarif;

namespace SilentScan.Core.Reporting.RuleDocs.Database;

internal static class AutoShrinkOn
{
    public static string RuleId => SarifRuleCatalog.DatabaseConfigurationRuleId(DatabaseConfigurationFindingKind.AutoShrinkOn);

    public static RuleDocContent Content { get; } = new(
        WhyItMatters: """
            `AUTO_SHRINK` periodically shrinks the database's data and log files to reclaim unused
            space - and for a database whose size fluctuates as part of normal operation (regular
            bulk loads followed by cleanup, index rebuilds, temp-heavy batch jobs), the engine ends
            up shrinking the file only for the workload to immediately re-grow it again. This is a
            long-established, essentially uncontroversial DBA anti-pattern: the repeated
            shrink/re-grow cycle causes constant index and file fragmentation churn for no durable
            space saving at all, since the space gets reclaimed and then re-allocated over and over.

            This is a database-level configuration fact, read once per scan directly from
            `sys.databases.is_auto_shrink_on` - only available when scanning a live, connected
            target, since there is no file-mode equivalent of "the database's own current
            configuration."
            """,
        HowToFixIt: """
            Turn AUTO_SHRINK OFF.
            """,
        Examples:
        [
            new RuleDocExample(
                Title: "A database with AUTO_SHRINK enabled",
                NoncompliantSql: """
                    ALTER DATABASE CURRENT SET AUTO_SHRINK ON;
                    """,
                NoncompliantExplanation: "Any workload whose size fluctuates gets its file repeatedly shrunk and immediately re-grown, causing constant fragmentation churn with no durable space saving.",
                CompliantSql: """
                    ALTER DATABASE CURRENT SET AUTO_SHRINK OFF;
                    """,
                CompliantExplanation: "With AUTO_SHRINK off, file size management becomes a deliberate, planned operation instead of a background process fighting the workload's own natural size fluctuation."),
        ]);
}
