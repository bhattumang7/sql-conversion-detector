using SilentScan.Core.Reporting.Sarif;

namespace SilentScan.Core.Reporting.RuleDocs.IndexShape;

internal static class CompositeIndexLeadingColumn
{
    public static string RuleId => SarifRuleCatalog.CompositeIndexLeadingColumnRuleId;

    public static RuleDocContent Content { get; } = new(
        WhyItMatters: """
            A real composite index's leading key column is never bound anywhere in the statement,
            while the query genuinely constrains one of the index's non-leading key columns. A
            composite index is a single B-tree keyed first by its leading column - without a bound
            on that column, this specific index cannot be seek-used for this predicate at all; the
            engine would have to scan the whole index to find rows matching only the later key
            column. This only fires when no OTHER usable index on the table leads with the same
            violating column either, so a table that has a real alternative seek path for this
            predicate is never flagged.
            """,
        HowToFixIt: """
            Either add a predicate on the index's leading key column so this index can be seeked, or
            create an index that leads with the column the query actually constrains.
            """,
        Examples:
        [
            new RuleDocExample(
                Title: "Filtering on a composite index's second key column only",
                NoncompliantSql: """
                    CREATE TABLE dbo.Orders (CustomerId INT NOT NULL, OrderDate DATE NOT NULL, OrderId INT NOT NULL PRIMARY KEY);
                    CREATE INDEX IX_Orders_Customer_Date ON dbo.Orders(CustomerId, OrderDate);

                    SELECT OrderId
                    FROM dbo.Orders
                    WHERE OrderDate = '2024-01-01';
                    """,
                NoncompliantExplanation: "IX_Orders_Customer_Date is keyed first by CustomerId - with no predicate on CustomerId anywhere in this statement, the index can't be seeked to satisfy a predicate on OrderDate alone.",
                CompliantSql: """
                    CREATE INDEX IX_Orders_Date ON dbo.Orders(OrderDate);

                    SELECT OrderId
                    FROM dbo.Orders
                    WHERE OrderDate = '2024-01-01';
                    """,
                CompliantExplanation: "An index that leads with OrderDate itself can be seeked directly by this predicate."),
        ]);
}
