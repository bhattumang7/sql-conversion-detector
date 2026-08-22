using SilentScan.Core.Predicates;
using SilentScan.Core.Reporting.Sarif;

namespace SilentScan.Core.Reporting.RuleDocs.QueryAntiPattern;

internal static class AlterTableSwitchFullTextIndexRestriction
{
    public static string RuleId => SarifRuleCatalog.QueryAntiPatternRuleId(QueryAntiPatternFindingKind.AlterTableSwitchFullTextIndexRestriction);

    public static RuleDocContent Content { get; } = new(
        WhyItMatters: """
            A full-text index is separate from the ordinary B-tree indexes whose compatibility
            ALTER TABLE ... SWITCH otherwise evaluates. It cannot move with a SWITCH, so the
            engine rejects the operation when either participating table has one.

            This remains true even when the two table definitions and ordinary indexes otherwise
            match exactly. The full-text index must be removed before the data movement operation.
            """,
        HowToFixIt: """
            Drop the full-text index from the source and target tables before the SWITCH. Recreate
            the required full-text index after the data movement has completed.
            """,
        Examples:
        [
            new RuleDocExample(
                Title: "A source table with a full-text index",
                NoncompliantSql: """
                    CREATE TABLE dbo.DocumentsStaging (Id INT NOT NULL PRIMARY KEY, Body NVARCHAR(4000) NOT NULL);
                    CREATE TABLE dbo.Documents (Id INT NOT NULL PRIMARY KEY, Body NVARCHAR(4000) NOT NULL);
                    CREATE FULLTEXT INDEX ON dbo.DocumentsStaging(Body) KEY INDEX PK__DocumentsStaging;

                    ALTER TABLE dbo.DocumentsStaging SWITCH TO dbo.Documents;
                    """,
                NoncompliantExplanation: "The source table has a full-text index, so the engine rejects the SWITCH with error 4918.",
                CompliantSql: """
                    DROP FULLTEXT INDEX ON dbo.DocumentsStaging;
                    ALTER TABLE dbo.DocumentsStaging SWITCH TO dbo.Documents;
                    """,
                CompliantExplanation: "After removing the full-text index, this restriction no longer prevents the SWITCH."),
        ]);
}
