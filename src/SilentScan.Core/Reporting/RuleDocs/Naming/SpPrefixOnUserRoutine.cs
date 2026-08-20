using SilentScan.Core.Reporting.Sarif;

namespace SilentScan.Core.Reporting.RuleDocs.Naming;

internal static class SpPrefixOnUserRoutine
{
    public static string RuleId => SarifRuleCatalog.NamingSpPrefixOnUserRoutineRuleId;

    public static RuleDocContent Content { get; } = new(
        WhyItMatters: """
            The `sp_` prefix is reserved by long-standing SQL Server convention for system-shipped
            procedures, and the engine treats it specially at resolution time: an unqualified call
            to a name starting with `sp_` is looked up in the `master` database FIRST, before the
            caller's own database, regardless of where the procedure actually lives. For a
            user-defined procedure this means every call pays a small extra resolution cost it
            doesn't need to, and - the sharper risk - if Microsoft ever ships a real system
            procedure with the same name in a future version, or if the calling context accidentally
            resolves against `master` instead of the intended database, the wrong procedure runs
            silently instead of raising an error.

            The check applies to both procedures and functions, and only to the routine's own name -
            a caller-side qualifier or the routine's owning schema doesn't change the risk, since
            `master`-first resolution happens on the bare name regardless.
            """,
        HowToFixIt: """
            Rename the routine to drop the sp_ prefix - a name with no special-cased resolution
            behavior removes both the extra master-database lookup and the collision risk.
            """,
        Examples:
        [
            new RuleDocExample(
                Title: "A user procedure named with the reserved sp_ prefix",
                NoncompliantSql: "CREATE PROCEDURE dbo.sp_DoSomething AS BEGIN SELECT 1; END",
                NoncompliantExplanation: "Every unqualified call to sp_DoSomething is resolved against master first - extra lookup cost today, and a silent collision risk if a real system procedure of the same name ever ships.",
                CompliantSql: "CREATE PROCEDURE dbo.DoSomething AS BEGIN SELECT 1; END",
                CompliantExplanation: "DoSomething resolves normally against the caller's own database, with no special-cased master lookup."),
        ]);
}
