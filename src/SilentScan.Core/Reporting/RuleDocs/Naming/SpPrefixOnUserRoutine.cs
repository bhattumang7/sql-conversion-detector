using SilentScan.Core.Reporting.Sarif;

namespace SilentScan.Core.Reporting.RuleDocs.Naming;

internal static class SpPrefixOnUserRoutine
{
    public static string RuleId => SarifRuleCatalog.NamingSpPrefixOnUserRoutineRuleId;

    public static RuleDocContent Content { get; } = new(
        WhyItMatters: """
            The `sp_` prefix is reserved by long-standing SQL Server convention for system-shipped
            procedures, and the engine treats it specially at resolution time: an unqualified call
            to a name starting with `sp_` is resolved against the caller's own database FIRST, and
            only falls through to the `master` database if no local match exists there. This means a
            user-defined `sp_`-prefixed routine silently shadows any real system procedure of the
            same name for every caller in that database - the local routine always wins, so the
            intended system procedure never runs, with no error raised. It also means the same
            unqualified call behaves inconsistently across databases: in the database that has the
            local override, callers get the local routine; in any other database lacking it, the
            same call instead falls through to whatever `master` provides.

            The check applies to both procedures and functions, and only to the routine's own name -
            a caller-side qualifier or the routine's owning schema doesn't change the risk, since
            local-first resolution happens on the bare name regardless.
            """,
        HowToFixIt: """
            Rename the routine to drop the sp_ prefix - a name with no special-cased resolution
            behavior removes both the shadowing risk and the cross-database inconsistency.
            """,
        Examples:
        [
            new RuleDocExample(
                Title: "A user procedure named with the reserved sp_ prefix",
                NoncompliantSql: "CREATE PROCEDURE dbo.sp_DoSomething AS BEGIN SELECT 1; END",
                NoncompliantExplanation: "Every unqualified call to sp_DoSomething in this database resolves to this local routine first, silently shadowing any real system procedure of the same name - and the same call would behave differently in a database that lacks this local override.",
                CompliantSql: "CREATE PROCEDURE dbo.DoSomething AS BEGIN SELECT 1; END",
                CompliantExplanation: "DoSomething resolves normally against the caller's own database, with no special-cased shadowing or fallback behavior."),
        ]);
}
