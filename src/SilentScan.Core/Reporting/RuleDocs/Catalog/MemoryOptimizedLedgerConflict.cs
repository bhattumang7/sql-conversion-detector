using SilentScan.Core.Reporting.Sarif;

namespace SilentScan.Core.Reporting.RuleDocs.Catalog;

internal static class MemoryOptimizedLedgerConflict
{
    public static string RuleId => SarifRuleCatalog.MemoryOptimizedLedgerConflictRuleId;

    public static RuleDocContent Content { get; } = new(
        WhyItMatters: """
            A CREATE TABLE statement specifies both MEMORY_OPTIMIZED = ON and LEDGER = ON.
            Oracle-confirmed (Msg 12359, "Ledger tables are not supported with memory
            optimized tables."): the CREATE TABLE statement never deploys. This is a
            catalog-only fact, decidable purely from the table's own WITH-clause options.
            """,
        HowToFixIt: """
            Drop MEMORY_OPTIMIZED = ON or LEDGER = ON from the table - the two are mutually
            exclusive, so pick whichever feature the table actually needs.
            """,
        Examples:
        [
            new RuleDocExample(
                Title: "A memory-optimized table with LEDGER = ON",
                NoncompliantSql: """
                    CREATE TABLE dbo.Accounts
                    (
                        AccountId INT NOT NULL PRIMARY KEY NONCLUSTERED,
                        Balance   DECIMAL(19,4) NOT NULL
                    ) WITH (MEMORY_OPTIMIZED = ON, LEDGER = ON);
                    """,
                NoncompliantExplanation: "Ledger tables are not supported with memory-optimized tables - the CREATE TABLE statement fails with error 12359 and never deploys.",
                CompliantSql: """
                    CREATE TABLE dbo.Accounts
                    (
                        AccountId INT NOT NULL PRIMARY KEY NONCLUSTERED,
                        Balance   DECIMAL(19,4) NOT NULL
                    ) WITH (MEMORY_OPTIMIZED = ON);
                    """,
                CompliantExplanation: "Dropping LEDGER = ON removes the incompatibility while keeping the table memory-optimized."),
        ]);
}
