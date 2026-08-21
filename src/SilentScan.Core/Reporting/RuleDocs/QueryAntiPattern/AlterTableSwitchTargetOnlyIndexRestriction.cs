using SilentScan.Core.Predicates;
using SilentScan.Core.Reporting.Sarif;

namespace SilentScan.Core.Reporting.RuleDocs.QueryAntiPattern;

internal static class AlterTableSwitchTargetOnlyIndexRestriction
{
    public static string RuleId => SarifRuleCatalog.QueryAntiPatternRuleId(QueryAntiPatternFindingKind.AlterTableSwitchTargetOnlyIndexRestriction);

    public static RuleDocContent Content { get; } = new(
        WhyItMatters: """
            Unlike the column, index, and constraint checks ALTER TABLE ... SWITCH otherwise
            applies (which are all "source and target must match"), XML and spatial indexes get a
            different, one-directional rule: the source table is allowed to carry an XML or
            spatial index, but the target table is never allowed to have one at all, regardless of
            whether the source has a matching one or not.

            This is easy to get backwards, since every other SWITCH prerequisite is about the two
            tables agreeing with each other. Here, agreement doesn't help - a target table with an
            XML or spatial index fails the SWITCH unconditionally, even if the source table's
            index is identical in every respect.
            """,
        HowToFixIt: """
            Remove the XML or spatial index from the target table before the SWITCH, or restructure
            which table plays which role - the table carrying the XML/spatial index needs to be the
            SWITCH source, never the target.
            """,
        Examples:
        [
            new RuleDocExample(
                Title: "A partitioned target table with a primary XML index",
                NoncompliantSql: """
                    CREATE TABLE dbo.DocsStaging (Id INT NOT NULL PRIMARY KEY, Body XML NULL);
                    CREATE TABLE dbo.Docs (Id INT NOT NULL PRIMARY KEY, Body XML NULL);
                    CREATE PRIMARY XML INDEX PXML_Docs ON dbo.Docs(Body);
                    -- (Docs is partitioned; DocsStaging holds a batch to load in.)

                    ALTER TABLE dbo.DocsStaging SWITCH TO dbo.Docs PARTITION 1;
                    -- Msg 4983: target table 'Docs' has an XML or spatial index 'PXML_Docs' on it.
                    -- Only source table can have XML or spatial indexes in the ALTER TABLE SWITCH
                    -- statement.
                    """,
                NoncompliantExplanation: "The target table carries the XML index - the engine refuses the SWITCH outright with error 4983, regardless of the source table's own indexes.",
                CompliantSql: """
                    CREATE PRIMARY XML INDEX PXML_DocsStaging ON dbo.DocsStaging(Body);
                    -- (No XML index on dbo.Docs, the target.)

                    ALTER TABLE dbo.DocsStaging SWITCH TO dbo.Docs PARTITION 1;
                    """,
                CompliantExplanation: "With the XML index only on the source table (never the target), this restriction no longer applies and the SWITCH proceeds to the engine's remaining checks."),
        ]);
}
