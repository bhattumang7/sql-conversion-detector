using SilentScan.Core.Reporting.Sarif;

namespace SilentScan.Core.Reporting.RuleDocs.Catalog;

internal static class MemoryOptimizedUnsupportedColumnType
{
    public static string RuleId => SarifRuleCatalog.MemoryOptimizedUnsupportedColumnTypeRuleId;

    public static RuleDocContent Content { get; } = new(
        WhyItMatters: """
            A memory-optimized table (CREATE TABLE ... WITH (MEMORY_OPTIMIZED = ON)) declares a
            column typed xml, json, sql_variant, text, ntext, image, timestamp/rowversion,
            hierarchyid, geometry, or geography.
            Oracle-confirmed (Msg 10794): none of these types are supported on a memory-optimized
            table at all, so the CREATE/ALTER TABLE statement never deploys - this is a hard
            structural incompatibility, not a performance concern. SQL Server 2025's native `json`
            type is rejected the same way as `xml`, oracle-confirmed directly, even though it is
            the newest type in this list. This is a catalog-only fact, detectable purely from the
            table's own column declarations with no query needed.
            """,
        HowToFixIt: """
            Retype the column to a type memory-optimized tables support (e.g. NVARCHAR(MAX) instead
            of XML/NTEXT, VARBINARY(MAX) instead of IMAGE), or move the column to a disk-based table
            instead if the type genuinely can't be substituted.
            """,
        Examples:
        [
            new RuleDocExample(
                Title: "An XML column on a memory-optimized table",
                NoncompliantSql: """
                    CREATE TABLE dbo.Documents
                    (
                        DocumentId INT NOT NULL PRIMARY KEY NONCLUSTERED,
                        Payload    XML NOT NULL
                    ) WITH (MEMORY_OPTIMIZED = ON);
                    """,
                NoncompliantExplanation: "XML is not one of the types memory-optimized tables support - the CREATE TABLE statement fails with error 10794 and never deploys.",
                CompliantSql: """
                    CREATE TABLE dbo.Documents
                    (
                        DocumentId INT           NOT NULL PRIMARY KEY NONCLUSTERED,
                        Payload    NVARCHAR(MAX) NOT NULL
                    ) WITH (MEMORY_OPTIMIZED = ON);
                    """,
                CompliantExplanation: "NVARCHAR(MAX) is supported on memory-optimized tables and can hold the same serialized content XML would have."),
        ]);
}
