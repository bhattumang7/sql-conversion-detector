using SilentScan.Core.Predicates;
using SilentScan.Core.Reporting.Sarif;

namespace SilentScan.Core.Reporting.RuleDocs.Database;

internal static class AutoUpdateStatisticsOff
{
    public static string RuleId => SarifRuleCatalog.DatabaseConfigurationRuleId(DatabaseConfigurationFindingKind.AutoUpdateStatisticsOff);

    public static RuleDocContent Content { get; } = new(
        WhyItMatters: """
            With `AUTO_UPDATE_STATISTICS` off, statistics never refresh as the underlying data
            changes - a plan compiled today against a table's current data distribution drifts
            further and further from reality the longer the database keeps running and being
            written to, with nothing correcting for it. `AUTO_UPDATE_STATISTICS` being ON is the
            engine's own out-of-the-box default, and this rule reports the same severity class as
            its `auto-create-statistics-off` sibling: a long-established, essentially
            uncontroversial anti-pattern to have turned off, not a deliberate operational choice the
            way Query Store's own settings are.

            This is a database-level configuration fact, read once per scan directly from
            `sys.databases.is_auto_update_stats_on` - only available when scanning a live, connected
            target, since there is no file-mode equivalent of "the database's own current
            configuration."
            """,
        HowToFixIt: """
            Turn AUTO_UPDATE_STATISTICS ON so statistics keep refreshing as the underlying data
            changes.
            """,
        Examples:
        [
            new RuleDocExample(
                Title: "A database with AUTO_UPDATE_STATISTICS turned off",
                NoncompliantSql: """
                    ALTER DATABASE CURRENT SET AUTO_UPDATE_STATISTICS OFF;
                    """,
                NoncompliantExplanation: "Statistics stop refreshing as the underlying data changes - every plan compiled against them drifts further from reality the longer the database runs, with nothing correcting for it.",
                CompliantSql: """
                    ALTER DATABASE CURRENT SET AUTO_UPDATE_STATISTICS ON;
                    """,
                CompliantExplanation: "Restores the engine's own default behavior - statistics automatically refresh as the underlying data changes."),
        ]);
}
