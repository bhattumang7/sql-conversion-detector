using SilentScan.Core.Reporting.Sarif;

namespace SilentScan.Core.Reporting.RuleDocs.Naming;

internal static class UnqualifiedCreate
{
    public static string RuleId => SarifRuleCatalog.NamingUnqualifiedCreateRuleId;

    public static RuleDocContent Content { get; } = new(
        WhyItMatters: """
            `CREATE PROCEDURE DoSomething`, `CREATE FUNCTION DoSomething`, or `CREATE VIEW MyView`
            with no schema prefix doesn't leave the object schema-less - SQL Server still assigns it
            a real owning schema, but that schema is resolved from the CONNECTING PRINCIPAL's own
            default schema at the moment the statement runs, not from anything visible in the script
            itself. Run the identical script as two different logins (or the same login after its
            default schema changes) and the object can land in two different schemas without a
            single character of the script changing. A later `dbo.DoSomething` reference then either
            resolves to a different object than the one just deployed, or fails to find it at all -
            and the deployment script itself gives no hint why, since it never named a schema to
            begin with.

            This makes the object's real identity depend on deployment-time environment rather than
            on anything checked into source control, which is exactly the kind of "looks fine in
            review, breaks depending on who ran it" defect this tool exists to catch statically.
            """,
        HowToFixIt: """
            Qualify the CREATE/ALTER statement with the object's real intended schema explicitly
            (e.g. `CREATE PROCEDURE dbo.DoSomething`), so its owning schema is a fact in the script
            itself, not a function of whoever happens to run it.
            """,
        Examples:
        [
            new RuleDocExample(
                Title: "An unqualified CREATE PROCEDURE",
                NoncompliantSql: "CREATE PROCEDURE DoSomething AS BEGIN SELECT 1; END",
                NoncompliantExplanation: "With no schema named, DoSomething's real owning schema is whatever the connecting principal's default schema happens to be at deployment time - not a fact visible in this script.",
                CompliantSql: "CREATE PROCEDURE dbo.DoSomething AS BEGIN SELECT 1; END",
                CompliantExplanation: "The procedure's owning schema (dbo) is now an explicit fact of the script, independent of who deploys it."),
            new RuleDocExample(
                Title: "An unqualified CREATE VIEW",
                NoncompliantSql: "CREATE VIEW MyView AS SELECT 1 AS Col;",
                NoncompliantExplanation: "Same risk as the procedure case - MyView's real schema depends on the deploying principal's default schema, invisible in the script.",
                CompliantSql: "CREATE VIEW dbo.MyView AS SELECT 1 AS Col;",
                CompliantExplanation: "The view's owning schema is now explicit and independent of the deploying principal."),
        ]);
}
