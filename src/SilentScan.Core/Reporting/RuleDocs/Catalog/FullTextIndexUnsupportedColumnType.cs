using SilentScan.Core.Predicates;
using SilentScan.Core.Reporting.Sarif;

namespace SilentScan.Core.Reporting.RuleDocs.Catalog;

internal static class FullTextIndexUnsupportedColumnType
{
    public static string RuleId => SarifRuleCatalog.FullTextIndexDdlRuleId(FullTextIndexDdlFindingKind.UnsupportedColumnType);

    public static RuleDocContent Content { get; } = new(
        WhyItMatters: """
            CREATE FULLTEXT INDEX names a column whose declared type isn't one of the types SQL
            Server's full-text engine can index. Oracle-confirmed (Msg 7670, "cannot be used for
            full-text search because it is not a character-based, XML, image, JSON or varbinary(max)
            type column or it is encrypted"): the statement never deploys, regardless of what data
            the column actually holds. The supported set is char, varchar, nchar, nvarchar, text,
            ntext, xml, image, json, and varbinary(max) specifically - a fixed-length varbinary
            (without MAX) is rejected the same as a numeric or datetime column would be. This is a
            catalog-only fact: the target table's own column type, already known from its CREATE/
            ALTER TABLE, decides it with no query needed.
            """,
        HowToFixIt: """
            Retype the column to one of the supported full-text types (typically NVARCHAR(MAX) or
            VARBINARY(MAX) with a TYPE COLUMN for binary content), or drop it from the full-text
            index's column list if it was never meant to be searched.
            """,
        Examples:
        [
            new RuleDocExample(
                Title: "A fixed-length varbinary column indexed for full-text search",
                NoncompliantSql: """
                    CREATE TABLE dbo.Documents
                    (
                        DocumentId INT           NOT NULL PRIMARY KEY,
                        Thumbnail  VARBINARY(100) NULL
                    );

                    CREATE FULLTEXT INDEX ON dbo.Documents(Thumbnail)
                        KEY INDEX PK__Documents;
                    """,
                NoncompliantExplanation: "VARBINARY(100) is fixed-length, not MAX-length - full-text indexing only accepts varbinary when it's declared MAX, so this fails with error 7670.",
                CompliantSql: """
                    CREATE TABLE dbo.Documents
                    (
                        DocumentId   INT           NOT NULL PRIMARY KEY,
                        Thumbnail    VARBINARY(MAX) NULL,
                        ThumbnailExt VARCHAR(10)    NULL
                    );

                    CREATE FULLTEXT INDEX ON dbo.Documents(Thumbnail TYPE COLUMN ThumbnailExt)
                        KEY INDEX PK__Documents;
                    """,
                CompliantExplanation: "VARBINARY(MAX) is a supported full-text type; pairing it with a TYPE COLUMN tells the engine what document format the binary content is in."),
        ]);
}
