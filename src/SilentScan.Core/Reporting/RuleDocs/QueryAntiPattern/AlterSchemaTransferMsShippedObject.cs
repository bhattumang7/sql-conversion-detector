using SilentScan.Core.Predicates;
using SilentScan.Core.Reporting.Sarif;

namespace SilentScan.Core.Reporting.RuleDocs.QueryAntiPattern;

internal static class AlterSchemaTransferMsShippedObject
{
    public static string RuleId => SarifRuleCatalog.QueryAntiPatternRuleId(QueryAntiPatternFindingKind.AlterSchemaTransferMsShippedObject);

    public static RuleDocContent Content { get; } = new(
        WhyItMatters: """
            ALTER SCHEMA ... TRANSFER moves an existing object into a different schema. Oracle-
            confirmed (Docker, SQL Server): when the named object is marked `is_ms_shipped = 1` in
            the catalog - a system view, system procedure, or any other object the engine itself
            owns - the statement fails unconditionally with error 15349, before any permission or
            destination-schema check runs. This is independent of which schema the object is
            currently in or which schema it's being moved to.

            This is easy to hit by accident: a dynamically-built object name, a typo that happens to
            match a `sys.*` catalog view, or a copy-pasted maintenance script that assumed a
            same-named object in the target application's own schema.
            """,
        HowToFixIt: """
            Do not attempt to move a Microsoft-shipped object into a different schema. If the
            intended target was a same-named user object, qualify the name explicitly so it does
            not resolve to the system-shipped one.
            """,
        Examples:
        [
            new RuleDocExample(
                Title: "ALTER SCHEMA TRANSFER against a system catalog view",
                NoncompliantSql: """
                    CREATE SCHEMA reporting;
                    GO
                    ALTER SCHEMA reporting TRANSFER OBJECT::sys.tables;
                    -- Msg 15349: Cannot transfer an MS Shipped object.
                    """,
                NoncompliantExplanation: "sys.tables is a Microsoft-shipped catalog view - the engine refuses the TRANSFER outright with error 15349, regardless of the destination schema.",
                CompliantSql: """
                    CREATE SCHEMA reporting;
                    GO
                    ALTER SCHEMA reporting TRANSFER OBJECT::dbo.SalesOrders;
                    """,
                CompliantExplanation: "dbo.SalesOrders is an ordinary user table, not Microsoft-shipped, so the TRANSFER proceeds to the engine's remaining (permission/dependency) checks."),
        ]);
}
