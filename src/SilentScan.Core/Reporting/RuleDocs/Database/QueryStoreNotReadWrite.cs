using SilentScan.Core.Predicates;
using SilentScan.Core.Reporting.Sarif;

namespace SilentScan.Core.Reporting.RuleDocs.Database;

internal static class QueryStoreNotReadWrite
{
    public static string RuleId => SarifRuleCatalog.DatabaseConfigurationRuleId(DatabaseConfigurationFindingKind.QueryStoreNotReadWrite);

    public static RuleDocContent Content { get; } = new(
        WhyItMatters: """
            Query Store is the engine's own built-in plan-regression and query-history diagnostic -
            when its actual state isn't `READ_WRITE` (it's OFF, READ_ONLY, or the edition/engine
            doesn't support it at all), that diagnostic is unavailable for this database, along with
            everything that depends on it: catching a plan regression after a statistics update,
            reviewing historical query performance, or forcing a known-good plan.

            Unlike this codebase's other database-configuration findings, this one is reported
            purely informationally rather than as a claimed anti-pattern: whether Query Store should
            be running genuinely depends on the workload and an operational choice some teams make
            deliberately - a very high-churn ad-hoc workload might disable it specifically to avoid
            its own overhead. This finding surfaces the current state as a fact worth confirming was
            a deliberate choice, not a universal recommendation to turn it on.

            This is a database-level configuration fact, read once per scan directly from
            `sys.database_query_store_options` - only available when scanning a live, connected
            target, since there is no file-mode equivalent of "the database's own current
            configuration."
            """,
        Examples:
        [
            new RuleDocExample(
                Title: "A database with Query Store turned off",
                NoncompliantSql: """
                    ALTER DATABASE CURRENT SET QUERY_STORE = OFF;
                    """,
                NoncompliantExplanation: "With Query Store off, the engine's own built-in plan-regression and query-history diagnostic is unavailable for this database - confirm this was a deliberate operational choice, not an oversight."),
        ]);
}
