using SilentScan.Core.Predicates;
using SilentScan.Core.Reporting.Sarif;

namespace SilentScan.Core.Reporting.RuleDocs.Predicates;

internal static class DropSchemaNotEmpty
{
    public static string RuleId => SarifRuleCatalog.DropProtectedObjectRuleId(DropProtectedObjectKind.SchemaNotEmpty);

    public static RuleDocContent Content { get; } = new(
        WhyItMatters: """
            DROP SCHEMA fails outright while any table, view, procedure, or function still lives in
            the schema (oracle-confirmed, Msg 3729, "Cannot drop schema '...' because it is being
            referenced by object '...'"). This scanner sees the schema own at least one such object
            elsewhere in the same scan and flags the DROP SCHEMA statement before it ever reaches the
            engine.
            """,
        HowToFixIt: """
            Drop or move every object out of the schema first, then drop the now-empty schema.
            """,
        Examples:
        [
            new RuleDocExample(
                Title: "DROP SCHEMA while the schema still owns a table",
                NoncompliantSql: """
                    CREATE SCHEMA Reporting;
                    GO
                    CREATE TABLE Reporting.MonthlyTotal (TotalId INT NOT NULL PRIMARY KEY);
                    GO
                    DROP SCHEMA Reporting;
                    """,
                NoncompliantExplanation: "Reporting.MonthlyTotal still references the schema, so DROP SCHEMA fails with Msg 3729.",
                CompliantSql: """
                    DROP TABLE Reporting.MonthlyTotal;
                    DROP SCHEMA Reporting;
                    """,
                CompliantExplanation: "The schema is genuinely empty by the time DROP SCHEMA runs, so it succeeds."),
        ]);
}
