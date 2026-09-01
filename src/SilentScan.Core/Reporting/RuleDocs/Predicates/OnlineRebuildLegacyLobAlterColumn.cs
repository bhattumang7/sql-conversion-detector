using SilentScan.Core.Predicates;
using SilentScan.Core.Reporting.Sarif;

namespace SilentScan.Core.Reporting.RuleDocs.Predicates;

internal static class OnlineRebuildLegacyLobAlterColumn
{
    public static string RuleId => SarifRuleCatalog.OnlineRebuildLegacyLobRuleId(OnlineRebuildLegacyLobKind.AlterColumnOnline);

    public static RuleDocContent Content { get; } = new(
        WhyItMatters: """
            ALTER TABLE ... ALTER COLUMN ... WITH (ONLINE = ON) is rejected whenever the column
            either currently is, or is being converted into, TEXT/NTEXT/IMAGE - oracle-confirmed
            (Msg 11427, "The online ALTER COLUMN operation cannot be performed for table '...'
            because column '...' currently has or is getting altered into an unsupported
            datatype: text, ntext, image, CLR type or FILESTREAM") both directions of that
            conversion fail online, decidable purely from the column's own before/after catalog
            types.
            """,
        HowToFixIt: """
            Drop ONLINE = ON and run the ALTER COLUMN offline, or migrate the column to
            VARCHAR(MAX)/NVARCHAR(MAX)/VARBINARY(MAX) before altering it online.
            """,
        Examples:
        [
            new RuleDocExample(
                Title: "Online ALTER COLUMN on an NTEXT column",
                NoncompliantSql: """
                    CREATE TABLE dbo.Article (ArticleId INT NOT NULL PRIMARY KEY, Body NTEXT NULL);
                    ALTER TABLE dbo.Article ALTER COLUMN Body NTEXT NULL WITH (ONLINE = ON);
                    """,
                NoncompliantExplanation: "dbo.Article.Body stays NTEXT across the ALTER, so the online operation fails with Msg 11427.",
                CompliantSql: """
                    CREATE TABLE dbo.Article (ArticleId INT NOT NULL PRIMARY KEY, Body NVARCHAR(MAX) NULL);
                    ALTER TABLE dbo.Article ALTER COLUMN Body NVARCHAR(200) NULL WITH (ONLINE = ON);
                    """,
                CompliantExplanation: "NVARCHAR(MAX) to NVARCHAR(200) never involves a legacy large-object type, so the online ALTER COLUMN succeeds."),
        ]);
}
