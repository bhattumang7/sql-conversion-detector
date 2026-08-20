using SilentScan.Core.Predicates;
using SilentScan.Core.Reporting.Sarif;

namespace SilentScan.Core.Reporting.RuleDocs.IndexDesign;

internal static class SubsumedIndex
{
    public static string RuleId => SarifRuleCatalog.IndexDesignRuleId(IndexDesignFindingKind.SubsumedIndex);

    public static RuleDocContent Content { get; } = new(
        WhyItMatters: """
            One active, unfiltered, non-columnstore index's key-column list is a proper (strictly
            shorter) ordered prefix of a second such index's own key-column list on the same table,
            with the shorter index's own INCLUDE columns already a subset of the longer index's
            INCLUDE columns, and the same uniqueness/kind between the two - the same precision guard
            the sibling `duplicate-index` rule uses. Any seek the shorter (subsumed) index could
            serve, the longer index can also serve: it carries every one of the shorter index's key
            columns as its own leading prefix, in the same order, plus everything the shorter index
            put in INCLUDE - so the shorter index isn't merely similar to the longer one, it's
            strictly redundant.

            Unlike `duplicate-index` (exact key-list equality), this rule catches the more common
            real-world shape: someone adds `IX_Orders_Status` on `(Status)`, and later - not knowing
            it already exists, or before it existed - adds `IX_Orders_Status_Date` on `(Status,
            OrderDate)`. The first index is now dead weight; the second one already answers every
            query the first one could.
            """,
        HowToFixIt: """
            Drop the narrower, redundant index - the wider index already covers it.
            """,
        Examples:
        [
            new RuleDocExample(
                Title: "A single-column index whose key is a prefix of a wider index",
                NoncompliantSql: """
                    CREATE TABLE dbo.Orders (Id INT NOT NULL, Status INT NOT NULL, OrderDate DATE NOT NULL);
                    CREATE NONCLUSTERED INDEX IX_Orders_Status ON dbo.Orders(Status);
                    CREATE NONCLUSTERED INDEX IX_Orders_Status_Date ON dbo.Orders(Status, OrderDate);
                    """,
                NoncompliantExplanation: "IX_Orders_Status's own key (Status) is exactly the leading prefix of IX_Orders_Status_Date's key (Status, OrderDate) - any seek the narrower index could serve, the wider one already serves too, so IX_Orders_Status is pure redundant write cost.",
                CompliantSql: """
                    CREATE TABLE dbo.Orders (Id INT NOT NULL, Status INT NOT NULL, OrderDate DATE NOT NULL);
                    CREATE NONCLUSTERED INDEX IX_Orders_Status_Date ON dbo.Orders(Status, OrderDate);
                    """,
                CompliantExplanation: "Dropping the subsumed narrower index removes the redundant write cost - every query that used to seek IX_Orders_Status can still seek IX_Orders_Status_Date on its own leading Status column."),
        ]);
}
