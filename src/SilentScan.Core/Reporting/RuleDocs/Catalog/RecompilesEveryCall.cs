using SilentScan.Core.Predicates;
using SilentScan.Core.Reporting.Sarif;

namespace SilentScan.Core.Reporting.RuleDocs.Catalog;

internal static class RecompilesEveryCall
{
    public static string RuleId => SarifRuleCatalog.ModuleCompileFlagRuleId(ModuleCompileFlagFindingKind.RecompilesEveryCall);

    public static RuleDocContent Content { get; } = new(
        WhyItMatters: """
            A module authored `WITH RECOMPILE` (a real, directly-read catalog flag -
            `sys.sql_modules.is_recompiled`) compiles a fresh execution plan every single time it
            runs and discards that plan immediately afterward, rather than caching it for reuse the
            way an ordinary module's plan is. That's sometimes a deliberate, correct choice - a
            module whose optimal plan genuinely varies wildly by parameter value, where a single
            cached plan would be wrong for most callers - but it has a real, easy-to-miss side
            effect worth surfacing: the module's own cost never accumulates in the plan cache at
            all, so it's invisible to any monitoring that reads `sys.dm_exec_cached_plans` or
            `sys.dm_exec_query_stats`. A module that's actually expensive per call can go completely
            unnoticed by cache-driven performance monitoring simply because it never has a plan
            sitting in the cache to be noticed.

            This is a purely structural catalog fact - directly read from `sys.sql_modules`, no plan
            shape or execution behavior involved - reported as a flag worth confirming was
            deliberate, not a claim that WITH RECOMPILE is itself wrong.
            """,
        Examples:
        [
            new RuleDocExample(
                Title: "A procedure authored WITH RECOMPILE",
                NoncompliantSql: """
                    CREATE PROCEDURE dbo.usp_RunAdHocReport
                        @Filter VARCHAR(100)
                    WITH RECOMPILE
                    AS
                    BEGIN
                        SELECT * FROM dbo.Orders WHERE Description LIKE '%' + @Filter + '%';
                    END;
                    """,
                NoncompliantExplanation: "This procedure's own execution cost never accumulates in the plan cache at all - if it turns out to be expensive per call, cache-driven monitoring (sys.dm_exec_cached_plans/sys.dm_exec_query_stats) will never surface it, since a fresh plan is compiled and discarded on every execution.",
                CompliantSql: null,
                CompliantExplanation: "Confirm WITH RECOMPILE is genuinely intended here (a real parameter-sensitivity problem this module needs), and monitor its cost through a mechanism other than the plan cache (e.g. Query Store, which captures per-execution statistics independent of plan caching) if it stays."),
        ]);
}
