using SilentScan.Core.Predicates;
using SilentScan.Core.Reporting.Sarif;

namespace SilentScan.Core.Reporting.RuleDocs.Database;

internal static class AutoCreateStatisticsOff
{
    public static string RuleId => SarifRuleCatalog.DatabaseConfigurationRuleId(DatabaseConfigurationFindingKind.AutoCreateStatisticsOff);

    public static RuleDocContent Content { get; } = new(
        WhyItMatters: """
            With `AUTO_CREATE_STATISTICS` off, the optimizer can no longer create a missing
            single-column statistics object on demand the first time it needs one - a predicate
            against a column with no existing statistics compiles against a guessed cardinality
            instead of a real histogram, which can steer the optimizer toward a badly-chosen plan
            for reasons that never show up in the query text itself. `AUTO_CREATE_STATISTICS` being
            ON is the engine's own out-of-the-box default; turning it off is a long-established,
            essentially uncontroversial anti-pattern to have done, not a workload-dependent choice
            the way Query Store's own settings are.

            This is a database-level configuration fact, read once per scan directly from
            `sys.databases.is_auto_create_stats_on` - only available when scanning a live, connected
            target, since there is no file-mode equivalent of "the database's own current
            configuration."
            """,
        HowToFixIt: """
            Turn AUTO_CREATE_STATISTICS ON so the optimizer can create a missing single-column
            statistics object on demand.
            """,
        Examples:
        [
            new RuleDocExample(
                Title: "A database with AUTO_CREATE_STATISTICS turned off",
                NoncompliantSql: """
                    ALTER DATABASE CURRENT SET AUTO_CREATE_STATISTICS OFF;
                    """,
                NoncompliantExplanation: "A predicate against a column with no existing statistics now compiles against a guessed cardinality instead of a real histogram, since the optimizer can no longer create one on demand.",
                CompliantSql: """
                    ALTER DATABASE CURRENT SET AUTO_CREATE_STATISTICS ON;
                    """,
                CompliantExplanation: "Restores the engine's own default behavior - a missing single-column statistics object is created automatically the first time the optimizer needs one."),
        ]);
}
