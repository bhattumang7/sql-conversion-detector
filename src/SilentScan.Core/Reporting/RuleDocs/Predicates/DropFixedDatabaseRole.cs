using SilentScan.Core.Predicates;
using SilentScan.Core.Reporting.Sarif;

namespace SilentScan.Core.Reporting.RuleDocs.Predicates;

internal static class DropFixedDatabaseRole
{
    public static string RuleId => SarifRuleCatalog.DropProtectedObjectRuleId(DropProtectedObjectKind.FixedDatabaseRole);

    public static RuleDocContent Content { get; } = new(
        WhyItMatters: """
            The engine's fixed database roles (db_owner, db_accessadmin, db_securityadmin,
            db_ddladmin, db_backupoperator, db_datareader, db_datawriter, db_denydatareader,
            db_denydatawriter) can never be dropped - oracle-confirmed (Msg 15150, "Cannot drop the
            role '...'"), unconditionally, regardless of membership or permission state. A DROP ROLE
            statement naming one of these is decidable purely from the role's own name, with no
            catalog lookup needed.
            """,
        HowToFixIt: """
            Remove the DROP ROLE statement for the fixed role - it cannot be dropped. Revoke
            membership or permissions on it instead if the goal is to take away access.
            """,
        Examples:
        [
            new RuleDocExample(
                Title: "DROP ROLE against a fixed database role",
                NoncompliantSql: """
                    DROP ROLE db_datawriter;
                    """,
                NoncompliantExplanation: "db_datawriter is a fixed database role - DROP ROLE against it always fails with Msg 15150.",
                CompliantSql: """
                    ALTER ROLE db_datawriter DROP MEMBER SomeUser;
                    """,
                CompliantExplanation: "Removing a member (or revoking permissions) achieves the access change without attempting to drop the fixed role itself."),
        ]);
}
