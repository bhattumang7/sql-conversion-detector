using SilentScan.Core.Predicates;
using SilentScan.Core.Reporting.Sarif;

namespace SilentScan.Core.Reporting.RuleDocs.IndexDesign;

internal static class HeapWithNonclusteredIndexes
{
    public static string RuleId => SarifRuleCatalog.IndexDesignRuleId(IndexDesignFindingKind.HeapWithNonclusteredIndexes);

    public static RuleDocContent Content { get; } = new(
        WhyItMatters: """
            A table with no clustered index anywhere (a heap) that nonetheless carries one or more
            real nonclustered indexes pays a real, documented cost every one of those nonclustered
            indexes wouldn't otherwise pay: on a clustered table, a nonclustered index's leaf row
            points back to the base row using the clustering key itself, but on a heap it has to use
            an 8-byte RID (row identifier) instead. That RID isn't as stable as a clustering key
            either - certain heap maintenance operations (most notably a forwarded-row pointer, left
            behind when a variable-length column grows past the row's original storage slot) can
            change it, adding an extra indirection hop to what should have been a direct lookup.

            This rule is deliberately narrower than "this table is a heap": a heap with ZERO indexes
            at all - a staging or bulk-load table, often a genuinely deliberate design choice for
            fast unindexed inserts - is excluded on purpose. This only fires once the table already
            has real nonclustered indexes paying the RID/forwarded-pointer cost, which is when the
            heap choice actually has a downside worth a second look.

            This is a catalog fact read directly from `sys.indexes` - live-mode only, since there is
            no file-mode equivalent of "does this table have a clustered index" without a real
            connected target to ask.
            """,
        HowToFixIt: """
            Add a clustered index to the table instead of leaving it a heap.
            """,
        Examples:
        [
            new RuleDocExample(
                Title: "A heap table with a nonclustered index",
                NoncompliantSql: """
                    CREATE TABLE dbo.Orders
                    (
                        Id     INT NOT NULL,
                        Status INT NOT NULL
                    );
                    CREATE NONCLUSTERED INDEX IX_Orders_Status ON dbo.Orders(Status);
                    -- No clustered index anywhere on dbo.Orders.
                    """,
                NoncompliantExplanation: "IX_Orders_Status has to use an 8-byte RID to point back to each base row instead of a clustering key, and that RID can move under heap maintenance (a forwarded-row pointer) - an extra indirection cost this index wouldn't pay on a clustered table.",
                CompliantSql: """
                    CREATE TABLE dbo.Orders
                    (
                        Id     INT NOT NULL PRIMARY KEY CLUSTERED,
                        Status INT NOT NULL
                    );
                    CREATE NONCLUSTERED INDEX IX_Orders_Status ON dbo.Orders(Status);
                    """,
                CompliantExplanation: "A clustered index gives IX_Orders_Status a stable clustering key to point back to instead of a RID, removing the forwarded-pointer risk entirely."),
        ]);
}
