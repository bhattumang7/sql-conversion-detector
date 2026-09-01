using SilentScan.Core.Reporting.Sarif;

namespace SilentScan.Core.Reporting.RuleDocs.Catalog;

internal static class MemoryOptimizedSchemaOnlyDurability
{
    public static string RuleId => SarifRuleCatalog.MemoryOptimizedSchemaOnlyDurabilityRuleId;

    public static RuleDocContent Content { get; } = new(
        WhyItMatters: """
            A memory-optimized table (CREATE TABLE ... WITH (MEMORY_OPTIMIZED = ON)) declares
            DURABILITY = SCHEMA_ONLY. Oracle-confirmed: the engine persists only the table's
            schema, not its rows - no transaction log records are generated for the table's data,
            and no checkpoint ever writes its rows to disk. Every row is lost on a server restart,
            a failover, or a database restore/attach, with no error or warning raised anywhere.
            This is a catalog-only fact, detectable purely from the table's own declared options
            with no query needed.
            """,
        HowToFixIt: """
            Declare the table WITH (DURABILITY = SCHEMA_AND_DATA) instead (or omit DURABILITY
            entirely, which defaults to SCHEMA_AND_DATA), unless every row genuinely is disposable
            across a restart - a session-scoped cache or staging table being the one legitimate use
            of SCHEMA_ONLY.
            """,
        Examples:
        [
            new RuleDocExample(
                Title: "A memory-optimized table declared SCHEMA_ONLY",
                NoncompliantSql: """
                    CREATE TABLE dbo.SessionCache
                    (
                        SessionId INT           NOT NULL PRIMARY KEY NONCLUSTERED,
                        Payload   NVARCHAR(4000) NULL
                    ) WITH (MEMORY_OPTIMIZED = ON, DURABILITY = SCHEMA_ONLY);
                    """,
                NoncompliantExplanation: "DURABILITY = SCHEMA_ONLY means every row in this table is lost on the next server restart or failover - only the table's schema survives.",
                CompliantSql: """
                    CREATE TABLE dbo.SessionCache
                    (
                        SessionId INT           NOT NULL PRIMARY KEY NONCLUSTERED,
                        Payload   NVARCHAR(4000) NULL
                    ) WITH (MEMORY_OPTIMIZED = ON, DURABILITY = SCHEMA_AND_DATA);
                    """,
                CompliantExplanation: "DURABILITY = SCHEMA_AND_DATA (the default when DURABILITY is omitted) logs and checkpoints the table's data like any other durable table, so rows survive a restart."),
        ]);
}
