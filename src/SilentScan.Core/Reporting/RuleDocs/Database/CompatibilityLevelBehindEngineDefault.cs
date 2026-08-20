using SilentScan.Core.Predicates;
using SilentScan.Core.Reporting.Sarif;

namespace SilentScan.Core.Reporting.RuleDocs.Database;

internal static class CompatibilityLevelBehindEngineDefault
{
    public static string RuleId => SarifRuleCatalog.DatabaseConfigurationRuleId(DatabaseConfigurationFindingKind.CompatibilityLevelBehindEngineDefault);

    public static RuleDocContent Content { get; } = new(
        WhyItMatters: """
            A database whose `compatibility_level` sits behind the connected engine instance's own
            current default is silently kept on an older cardinality estimator and query-optimizer
            behavior nobody actually chose on purpose - each compatibility-level jump is itself a
            real, sometimes plan-changing behavior shift, and a database that never had its level
            raised after an engine upgrade just keeps accumulating that gap indefinitely.

            "The engine's own current default" is determined precisely here, not guessed: rather
            than a `SERVERPROPERTY('ProductMajorVersion')`-derived version-number mapping (a mapping
            that silently goes stale the day a new engine version ships, or differs across Azure SQL
            Database edition history, or shifts with a cumulative update), this tool reads
            `compatibility_level` live from the `model` system database on the SAME connected
            instance - an unqualified, server-scoped `sys.databases` row visible from any
            database's connection, no context switch required. `model` is exactly what the engine
            itself clones every newly created database from, so its compatibility level IS this
            specific engine instance's own real current default, not an assumption baked into this
            tool's own code.

            This finding deliberately does not claim a specific target level is correct for this
            workload - a deliberate pin at an older level for a known regression is legitimate. What
            it reports is the gap itself: a level that has silently fallen behind rather than been
            chosen, which is worth confirming either way.

            This is a database-level configuration fact, read once per scan directly from
            `sys.databases.compatibility_level` - only available when scanning a live, connected
            target, since there is no file-mode equivalent of "the database's own current
            configuration."
            """,
        HowToFixIt: """
            Raise the database's compatibility level to the connected engine instance's own current
            default.
            """,
        Examples:
        [
            new RuleDocExample(
                Title: "A database left behind the connected engine's own current default",
                NoncompliantSql: """
                    -- The connected instance's own `model` database (what every new database is
                    -- cloned from) is at compatibility level 160; this database was never raised
                    -- after the engine's last upgrade and still sits at 150.
                    ALTER DATABASE CURRENT SET COMPATIBILITY_LEVEL = 150;
                    """,
                NoncompliantExplanation: "This database is silently kept on an older cardinality estimator and optimizer behavior than the engine instance's own current default - a gap that accumulated rather than one anyone deliberately chose.",
                CompliantSql: """
                    ALTER DATABASE CURRENT SET COMPATIBILITY_LEVEL = 160;
                    """,
                CompliantExplanation: "Raising the compatibility level to match the engine instance's own current default closes the gap - a deliberate pin at an older level for a known regression would instead be a documented, intentional choice, not a silent accumulation."),
        ]);
}
