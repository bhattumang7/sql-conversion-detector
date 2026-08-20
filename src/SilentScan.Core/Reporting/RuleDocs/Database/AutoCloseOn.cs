using SilentScan.Core.Predicates;
using SilentScan.Core.Reporting.Sarif;

namespace SilentScan.Core.Reporting.RuleDocs.Database;

internal static class AutoCloseOn
{
    public static string RuleId => SarifRuleCatalog.DatabaseConfigurationRuleId(DatabaseConfigurationFindingKind.AutoCloseOn);

    public static RuleDocContent Content { get; } = new(
        WhyItMatters: """
            `AUTO_CLOSE` tears down the database's own connection and buffer-pool state entirely
            after the last connection to it closes, and rebuilds that state from scratch the moment
            a new connection opens. That rebuild is real, added latency landing on whichever
            connection happens to be first after a quiet period - the buffer pool has to be
            repopulated, files reopened, and internal state reinitialized, all before that first
            query can even begin running. For any database that isn't connected to constantly, this
            turns an ordinary "first query after a pause" into a noticeably slower one, invisible in
            steady-state monitoring since it only shows up on that specific first connection.

            This is a database-level configuration fact, read once per scan directly from
            `sys.databases.is_auto_close_on` - only available when scanning a live, connected
            target, since there is no file-mode equivalent of "the database's own current
            configuration."
            """,
        HowToFixIt: """
            Turn AUTO_CLOSE OFF.
            """,
        Examples:
        [
            new RuleDocExample(
                Title: "A database with AUTO_CLOSE enabled",
                NoncompliantSql: """
                    ALTER DATABASE CURRENT SET AUTO_CLOSE ON;
                    """,
                NoncompliantExplanation: "The database's connection/buffer-pool state is torn down after the last connection closes - the next connection pays the full cost of rebuilding it from scratch before its first query can even begin.",
                CompliantSql: """
                    ALTER DATABASE CURRENT SET AUTO_CLOSE OFF;
                    """,
                CompliantExplanation: "With AUTO_CLOSE off, the database's state stays warm between connections instead of being rebuilt from scratch on every reconnect."),
        ]);
}
