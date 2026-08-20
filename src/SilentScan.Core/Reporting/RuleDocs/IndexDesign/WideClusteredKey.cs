using SilentScan.Core.Predicates;
using SilentScan.Core.Reporting.Sarif;

namespace SilentScan.Core.Reporting.RuleDocs.IndexDesign;

internal static class WideClusteredKey
{
    public static string RuleId => SarifRuleCatalog.IndexDesignRuleId(IndexDesignFindingKind.WideClusteredKey);

    public static RuleDocContent Content { get; } = new(
        WhyItMatters: """
            A clustered index's key isn't just used by the clustered index itself - every
            nonclustered index on the table carries a full copy of that key in every one of its own
            leaf rows, as the locator it uses to find the base row. A wide clustering key (more than
            3 key columns, or more than 16 estimated total key bytes) multiplies its own storage and
            I/O cost across every other index on the table, not just the clustered one - a cost that
            compounds with however many nonclustered indexes the table happens to carry.

            The byte-width estimate is computed from the same column-type/length modeling this
            catalog already reads for every other purpose - a LOB/MAX/unresolved column type never
            gets a guessed-at contribution, so the estimate is always a safe lower bound. These
            thresholds were calibrated against the real distribution of clustered indexes in this
            project's own local production-shaped test database before being kept, and this finding
            is reported at Medium confidence rather than High - a threshold-based judgment call is
            inherently softer than a structurally-provable fact like the sibling
            `non-unique-clustered-index` rule.
            """,
        HowToFixIt: """
            Narrow the clustered index's key to fewer/smaller columns - every nonclustered index
            carries a full copy of it.
            """,
        Examples:
        [
            new RuleDocExample(
                Title: "A clustered index with four key columns",
                NoncompliantSql: """
                    CREATE TABLE dbo.OrderLines
                    (
                        TenantId  INT NOT NULL,
                        OrderId   INT NOT NULL,
                        LineType  INT NOT NULL,
                        LineId    INT NOT NULL,
                        Quantity  INT NOT NULL
                    );
                    CREATE CLUSTERED INDEX IX_OrderLines_Key
                        ON dbo.OrderLines(TenantId, OrderId, LineType, LineId);
                    """,
                NoncompliantExplanation: "Four key columns exceeds this rule's 3-column threshold - every nonclustered index later added to dbo.OrderLines will carry a full copy of all four columns in its own leaf rows.",
                CompliantSql: """
                    CREATE TABLE dbo.OrderLines
                    (
                        Id        INT IDENTITY NOT NULL,
                        TenantId  INT NOT NULL,
                        OrderId   INT NOT NULL,
                        LineType  INT NOT NULL,
                        LineId    INT NOT NULL,
                        Quantity  INT NOT NULL
                    );
                    CREATE CLUSTERED INDEX IX_OrderLines_Key ON dbo.OrderLines(Id);
                    CREATE UNIQUE NONCLUSTERED INDEX UX_OrderLines_Natural
                        ON dbo.OrderLines(TenantId, OrderId, LineType, LineId);
                    """,
                CompliantExplanation: "A single-column surrogate clustering key keeps the row locator every nonclustered index carries narrow, while the original natural-key uniqueness is preserved as its own separate nonclustered index."),
        ]);
}
