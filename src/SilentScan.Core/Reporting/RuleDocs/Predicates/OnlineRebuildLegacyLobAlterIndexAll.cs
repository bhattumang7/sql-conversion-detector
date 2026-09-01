using SilentScan.Core.Predicates;
using SilentScan.Core.Reporting.Sarif;

namespace SilentScan.Core.Reporting.RuleDocs.Predicates;

internal static class OnlineRebuildLegacyLobAlterIndexAll
{
    public static string RuleId => SarifRuleCatalog.OnlineRebuildLegacyLobRuleId(OnlineRebuildLegacyLobKind.AlterIndexAllRebuild);

    public static RuleDocContent Content { get; } = new(
        WhyItMatters: """
            ALTER INDEX ALL ... REBUILD WITH (ONLINE = ON) rebuilds every index on the table,
            including its own clustered index or heap, which always touches every column -
            oracle-confirmed (Msg 2725, "An online operation cannot be performed for index '...'
            because the index contains column '...' of data type text, ntext, image or
            FILESTREAM") a TEXT/NTEXT/IMAGE column on the table makes the online rebuild fail
            outright, decidable purely from the table's own catalog column types. A single named
            index's own online rebuild is unaffected unless that specific index carries the
            legacy large-object column.
            """,
        HowToFixIt: """
            Drop ONLINE = ON and rebuild offline, rebuild only the specific indexes that do not
            carry the legacy large-object column, or migrate the TEXT/NTEXT/IMAGE column to
            VARCHAR(MAX)/NVARCHAR(MAX)/VARBINARY(MAX) first, which online rebuild does support.
            """,
        Examples:
        [
            new RuleDocExample(
                Title: "Online rebuild of every index on a table carrying an IMAGE column",
                NoncompliantSql: """
                    CREATE TABLE dbo.Attachment (AttachmentId INT NOT NULL PRIMARY KEY, Content IMAGE NULL);
                    CREATE INDEX IX_Attachment_Content ON dbo.Attachment (AttachmentId);
                    ALTER INDEX ALL ON dbo.Attachment REBUILD WITH (ONLINE = ON);
                    """,
                NoncompliantExplanation: "dbo.Attachment.Content is IMAGE, so rebuilding the clustered index as part of ALL fails with Msg 2725.",
                CompliantSql: """
                    ALTER INDEX IX_Attachment_Content ON dbo.Attachment REBUILD WITH (ONLINE = ON);
                    """,
                CompliantExplanation: "Rebuilding only the named nonclustered index, which does not carry the IMAGE column, succeeds online."),
        ]);
}
