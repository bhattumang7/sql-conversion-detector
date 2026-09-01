using SilentScan.Core.Predicates;
using SilentScan.Core.Reporting.Sarif;

namespace SilentScan.Core.Reporting.RuleDocs.Predicates;

internal static class OnlineRebuildLegacyLobAlterTable
{
    public static string RuleId => SarifRuleCatalog.OnlineRebuildLegacyLobRuleId(OnlineRebuildLegacyLobKind.AlterTableRebuild);

    public static RuleDocContent Content { get; } = new(
        WhyItMatters: """
            ALTER TABLE ... REBUILD WITH (ONLINE = ON) rebuilds the table's own clustered index or
            heap, which always touches every column - oracle-confirmed (Msg 2725, "An online
            operation cannot be performed for index '...' because the index contains column '...'
            of data type text, ntext, image or FILESTREAM") a TEXT/NTEXT/IMAGE column on the table
            makes the online rebuild fail outright, decidable purely from the table's own catalog
            column types.
            """,
        HowToFixIt: """
            Drop ONLINE = ON and rebuild offline, or migrate the TEXT/NTEXT/IMAGE column to
            VARCHAR(MAX)/NVARCHAR(MAX)/VARBINARY(MAX) first, which online rebuild does support.
            """,
        Examples:
        [
            new RuleDocExample(
                Title: "Online rebuild of a table carrying an NTEXT column",
                NoncompliantSql: """
                    CREATE TABLE dbo.Article (ArticleId INT NOT NULL PRIMARY KEY, Body NTEXT NULL);
                    ALTER TABLE dbo.Article REBUILD WITH (ONLINE = ON);
                    """,
                NoncompliantExplanation: "dbo.Article.Body is NTEXT, so the online rebuild fails with Msg 2725.",
                CompliantSql: """
                    CREATE TABLE dbo.Article (ArticleId INT NOT NULL PRIMARY KEY, Body NVARCHAR(MAX) NULL);
                    ALTER TABLE dbo.Article REBUILD WITH (ONLINE = ON);
                    """,
                CompliantExplanation: "NVARCHAR(MAX) is online-rebuild-eligible, so the statement succeeds."),
        ]);
}
