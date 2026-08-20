using SilentScan.Core.Predicates;
using SilentScan.Core.Reporting.Sarif;

namespace SilentScan.Core.Reporting.RuleDocs.IndexDesign;

internal static class FilterColumnNotInIndex
{
    public static string RuleId => SarifRuleCatalog.IndexDesignRuleId(IndexDesignFindingKind.FilterColumnNotInIndex);

    public static RuleDocContent Content { get; } = new(
        WhyItMatters: """
            A filtered index's own filter predicate can only be substituted by the optimizer for a
            query whose own WHERE clause restates (or logically implies) that same predicate. When
            the filter predicate references a column the index itself does not carry - neither as a
            key column nor as an INCLUDE column - the optimizer would additionally need to re-derive
            that column's value from the base table just to confirm the filter still holds, which
            defeats the covering benefit a filtered index exists for in the first place.

            This finding only fires when the filter's own definition text reparses cleanly - a
            filter this pass cannot parse is left unanalyzed rather than guessed at, the same
            "never guess" discipline the sibling `duplicate-index`/`subsumed-index` rules already
            apply to an unfiltered index's own key/INCLUDE comparison.
            """,
        HowToFixIt: """
            Add the filtered index's own filter column(s) to its key or INCLUDE list.
            """,
        Examples:
        [
            new RuleDocExample(
                Title: "A filtered index whose filter column isn't carried by the index itself",
                NoncompliantSql: """
                    CREATE TABLE dbo.Orders (CustomerId INT NOT NULL, IsActive BIT NOT NULL);
                    CREATE NONCLUSTERED INDEX IX_Orders_Active
                        ON dbo.Orders (CustomerId)
                        WHERE IsActive = 1;
                    """,
                NoncompliantExplanation: "The filter references IsActive, but the index only carries CustomerId - IsActive is neither a key column nor an INCLUDE column, so the optimizer can't cheaply confirm the filter still holds for a query that doesn't already carry IsActive itself.",
                CompliantSql: """
                    CREATE NONCLUSTERED INDEX IX_Orders_Active
                        ON dbo.Orders (CustomerId)
                        INCLUDE (IsActive)
                        WHERE IsActive = 1;
                    """,
                CompliantExplanation: "IsActive is now carried by the index as an INCLUDE column, so the optimizer can confirm the filter still holds without re-reading the base table."),
        ]);
}
