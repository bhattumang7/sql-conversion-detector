using SilentScan.Core.Reporting.Sarif;

namespace SilentScan.Core.Reporting.RuleDocs.Catalog;

internal static class MemoryOptimizedUtf8Collation
{
    public static string RuleId => SarifRuleCatalog.MemoryOptimizedUtf8CollationRuleId;

    public static RuleDocContent Content { get; } = new(
        WhyItMatters: """
            A memory-optimized table (CREATE TABLE ... WITH (MEMORY_OPTIMIZED = ON)) declares a
            char or varchar column whose collation carries the _UTF8 flag. Oracle-confirmed
            (Msg 12356, "Comparison, sorting, and manipulation of character strings that use a
            UTF8 collation is not supported with memory optimized tables."): the CREATE/ALTER
            TABLE statement never deploys, whether or not the table is ever touched by a
            natively compiled module. This is a catalog-only fact, detectable purely from the
            column's own declared type and its own or the database's effective collation.
            """,
        HowToFixIt: """
            Use a non-UTF8 collation for the column, or convert it to nvarchar/nchar if
            UTF-8-capable Unicode storage is genuinely required - the UTF8 flag only affects
            char/varchar storage, so nvarchar/nchar columns are unaffected by this restriction.
            """,
        Examples:
        [
            new RuleDocExample(
                Title: "A varchar column with a UTF-8 collation on a memory-optimized table",
                NoncompliantSql: """
                    CREATE TABLE dbo.Documents
                    (
                        DocumentId INT         NOT NULL PRIMARY KEY NONCLUSTERED,
                        Title      VARCHAR(100) COLLATE Latin1_General_100_CI_AS_SC_UTF8 NOT NULL
                    ) WITH (MEMORY_OPTIMIZED = ON);
                    """,
                NoncompliantExplanation: "A UTF8 collation on a char/varchar column is not supported on a memory-optimized table - the CREATE TABLE statement fails with error 12356 and never deploys.",
                CompliantSql: """
                    CREATE TABLE dbo.Documents
                    (
                        DocumentId INT         NOT NULL PRIMARY KEY NONCLUSTERED,
                        Title      VARCHAR(100) COLLATE Latin1_General_100_CI_AS NOT NULL
                    ) WITH (MEMORY_OPTIMIZED = ON);
                    """,
                CompliantExplanation: "Dropping the _UTF8 flag from the collation removes the incompatibility while keeping the same non-Unicode storage."),
        ]);
}
