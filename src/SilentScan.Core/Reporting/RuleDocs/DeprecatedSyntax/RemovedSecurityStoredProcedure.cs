using SilentScan.Core.Predicates;
using SilentScan.Core.Reporting.Sarif;

namespace SilentScan.Core.Reporting.RuleDocs.DeprecatedSyntax;

internal static class RemovedSecurityStoredProcedure
{
    public static string RuleId => SarifRuleCatalog.DeprecatedSyntaxRuleId(DeprecatedSyntaxFindingKind.RemovedSecurityStoredProcedure);

    public static RuleDocContent Content { get; } = new(
        WhyItMatters: """
            A legacy security-administration system stored procedure is invoked, superseded by
            CREATE LOGIN/CREATE USER/ALTER ROLE - some names in this family are already fully removed
            from current SQL Server versions. A deployment script that still calls one of these will
            fail outright on a version where it has been removed.
            """,
        HowToFixIt: "Use CREATE LOGIN/CREATE USER/ALTER ROLE instead of the legacy security-administration stored procedure.",
        Examples:
        [
            new RuleDocExample(
                Title: "A legacy security stored procedure call",
                NoncompliantSql: "EXEC sp_addlogin 'app_user', 'a-strong-password';",
                NoncompliantExplanation: "sp_addlogin is a legacy security-administration procedure; this family of procedures is deprecated and some members are already removed on current SQL Server versions.",
                CompliantSql: "CREATE LOGIN app_user WITH PASSWORD = 'a-strong-password';",
                CompliantExplanation: "CREATE LOGIN is the current, supported way to create a login."),
        ]);
}
