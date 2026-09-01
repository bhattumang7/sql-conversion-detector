using SilentScan.Core.Predicates;
using SilentScan.Core.Reporting.Sarif;

namespace SilentScan.Core.Reporting.RuleDocs.Predicates;

internal static class OnlineRebuildLegacyLobDropIndex
{
    public static string RuleId => SarifRuleCatalog.OnlineRebuildLegacyLobRuleId(OnlineRebuildLegacyLobKind.DropIndexOnline);

    public static RuleDocContent Content { get; } = new(
        WhyItMatters: """
            DROP INDEX ... WITH (ONLINE = ON) against a clustered index always touches every
            column of the table, the same way an online rebuild does - oracle-confirmed
            (Msg 2725, "An online operation cannot be performed for index '...' because the
            index contains column '...' of data type text, ntext, image or FILESTREAM") a
            TEXT/NTEXT/IMAGE column anywhere on the table makes the online drop fail outright,
            decidable purely from the table's own catalog column types. A nonclustered index's
            online drop is rejected for an unrelated reason (only clustered indexes can be
            dropped online at all) and is not this rule's concern.
            """,
        HowToFixIt: """
            Drop ONLINE = ON and drop the index offline, or migrate the TEXT/NTEXT/IMAGE column
            to VARCHAR(MAX)/NVARCHAR(MAX)/VARBINARY(MAX) first, which online index drop does
            support.
            """,
        Examples:
        [
            new RuleDocExample(
                Title: "Online drop of a clustered index on a table carrying an NTEXT column",
                NoncompliantSql: """
                    CREATE TABLE dbo.Article (ArticleId INT NOT NULL, Body NTEXT NULL);
                    CREATE CLUSTERED INDEX CIX_Article ON dbo.Article (ArticleId);
                    DROP INDEX CIX_Article ON dbo.Article WITH (ONLINE = ON);
                    """,
                NoncompliantExplanation: "dbo.Article.Body is NTEXT, so dropping the clustered index online fails with Msg 2725.",
                CompliantSql: """
                    CREATE TABLE dbo.Article (ArticleId INT NOT NULL, Body NVARCHAR(MAX) NULL);
                    CREATE CLUSTERED INDEX CIX_Article ON dbo.Article (ArticleId);
                    DROP INDEX CIX_Article ON dbo.Article WITH (ONLINE = ON);
                    """,
                CompliantExplanation: "NVARCHAR(MAX) is online-eligible, so the clustered index drops online successfully."),
        ]);
}
