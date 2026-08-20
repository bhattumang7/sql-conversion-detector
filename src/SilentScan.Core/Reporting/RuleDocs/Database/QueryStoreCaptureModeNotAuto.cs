using SilentScan.Core.Predicates;
using SilentScan.Core.Reporting.Sarif;

namespace SilentScan.Core.Reporting.RuleDocs.Database;

internal static class QueryStoreCaptureModeNotAuto
{
    public static string RuleId => SarifRuleCatalog.DatabaseConfigurationRuleId(DatabaseConfigurationFindingKind.QueryStoreCaptureModeNotAuto);

    public static RuleDocContent Content { get; } = new(
        WhyItMatters: """
            When Query Store is actively running (`READ_WRITE`), its capture mode controls which
            queries it bothers recording - `AUTO` (the default) skips ad-hoc, infrequently-executed,
            or resource-cheap queries to keep Query Store's own storage and overhead bounded, while
            `ALL` captures everything and `NONE` captures nothing new. A capture mode other than
            AUTO is reported here purely informationally, the same workload-dependent reasoning as
            the sibling `query-store-not-read-write` finding: `ALL` is a deliberate, real choice
            some teams prefer specifically for active troubleshooting, when they need every query
            captured regardless of cost, not a mistake.

            This finding is only ever evaluated when Query Store's own actual state IS
            `READ_WRITE` - reporting a capture-mode complaint about a Query Store that isn't even
            running would be a confusing, redundant second finding for the same underlying fact the
            sibling rule already reports.

            This is a database-level configuration fact, read once per scan directly from
            `sys.database_query_store_options` - only available when scanning a live, connected
            target, since there is no file-mode equivalent of "the database's own current
            configuration."
            """,
        Examples:
        [
            new RuleDocExample(
                Title: "Query Store running with capture mode set to ALL",
                NoncompliantSql: """
                    ALTER DATABASE CURRENT SET QUERY_STORE (QUERY_CAPTURE_MODE = ALL);
                    """,
                NoncompliantExplanation: "ALL captures every query regardless of cost or frequency - a real, deliberate choice for active troubleshooting, but worth confirming it wasn't left on by accident once the troubleshooting is done, since it carries more storage and overhead than AUTO."),
        ]);
}
