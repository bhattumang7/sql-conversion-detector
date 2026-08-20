using SilentScan.Core.Predicates;
using SilentScan.Core.Reporting.Sarif;

namespace SilentScan.Core.Reporting.RuleDocs.IndexDesign;

internal static class ManyKeyColumnsIndex
{
    public static string RuleId => SarifRuleCatalog.IndexDesignRuleId(IndexDesignFindingKind.ManyKeyColumnsIndex);

    public static RuleDocContent Content { get; } = new(
        WhyItMatters: """
            A single active, non-clustered, non-columnstore index carrying at least 7 key columns
            means every one of those columns is carried in every leaf-level lookup and every write
            maintenance operation against this specific index - a real, ongoing cost that scales with
            key width, independent of the sibling `wide-clustered-key` rule's own concern.

            This is deliberately distinct from `wide-clustered-key`: that rule is scoped specifically
            to the CLUSTERED key, at its own tighter 3-column/16-byte thresholds (since a wide
            clustered key multiplies its cost across every OTHER index on the table too), while this
            rule covers any nonclustered index individually crossing its own, separate threshold. A
            clustered index that also happens to clear this rule's own 7-column threshold is never
            re-reported here - excluded by construction, not an overlap left unhandled.
            """,
        HowToFixIt: """
            Reduce the number of key columns in the index to only those actually needed for
            seeking/ordering.
            """,
        Examples:
        [
            new RuleDocExample(
                Title: "A nonclustered index with seven key columns",
                NoncompliantSql: """
                    CREATE NONCLUSTERED INDEX IX_Orders_Wide
                        ON dbo.Orders(TenantId, RegionId, Status, CustomerId, OrderType, Priority, CreatedBy);
                    """,
                NoncompliantExplanation: "Seven key columns means every one of them is carried in every leaf-level row of this index and touched on every write - most queries seeking on this index likely only need the first few, leading, most-selective columns.",
                CompliantSql: """
                    CREATE NONCLUSTERED INDEX IX_Orders_Narrow ON dbo.Orders(TenantId, Status)
                        INCLUDE (CustomerId, OrderType, Priority, CreatedBy);
                    """,
                CompliantExplanation: "Only the columns actually needed for seeking/ordering stay in the key; the rest move to INCLUDE, where they're still available for the query but no longer widen the B-tree's own key comparisons."),
        ]);
}
