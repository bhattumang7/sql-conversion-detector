using SilentScan.Core.Reporting.Sarif;

namespace SilentScan.Core.Reporting.RuleDocs.Index;

internal static class KeyLookupProneIndex
{
    public static string RuleId => SarifRuleCatalog.IndexCoverageKeyLookupProneIndexRuleId;

    public static RuleDocContent Content { get; } = new(
        WhyItMatters: """
            A nonclustered index only stores its own key columns (plus any explicit `INCLUDE`d
            ones) - anything else a query needs has to be fetched separately, one row at a time,
            from the clustered index (or heap) by following the row locator the nonclustered index
            entry carries. This is a Key Lookup (RID Lookup on a heap), and it turns what looks like
            a cheap index seek into a seek-plus-lookup pair executed once per matching row: fine for
            a handful of rows, a real cost once the seek matches thousands or more, since each
            lookup is its own random I/O against the base table rather than a sequential scan of
            already-fetched index pages.

            This rule fires when a table has exactly one nonclustered index whose leading column(s)
            could serve a query's own equality predicate, and that index does not cover every other
            column the same statement references on that table - the seek is real, but every matched
            row pays for a lookup to retrieve the columns the index itself doesn't carry. It's
            oracle-confirmed directly: the exact shape this rule flags was run against a real engine
            and its plan XML shows `Lookup="1"` on the resulting operator, and widening the index to
            cover the missing columns was confirmed to remove it from the plan. When a table has more
            than one candidate index for the same predicate, the rule declines rather than guess
            which one the optimizer would actually pick.
            """,
        HowToFixIt: """
            Widen the nonclustered index to cover every column the statement references on that
            table - either by adding the missing columns to the index key, or (more cheaply, when
            they're only ever read, never filtered or sorted on) as `INCLUDE` columns - so the Key
            Lookup disappears from the plan.
            """,
        Examples:
        [
            new RuleDocExample(
                Title: "A single-column index leaves two selected columns uncovered",
                NoncompliantSql: """
                    CREATE TABLE dbo.Orders
                    (
                        Id     INT NOT NULL PRIMARY KEY,
                        Status INT NOT NULL,
                        Total  DECIMAL(10,2) NOT NULL,
                        Notes  VARCHAR(200) NOT NULL
                    );
                    CREATE NONCLUSTERED INDEX IX_Orders_Status ON dbo.Orders(Status);

                    SELECT Id, Status, Total, Notes
                    FROM dbo.Orders
                    WHERE Status = 2;
                    """,
                NoncompliantExplanation: "IX_Orders_Status only carries Status - Total and Notes aren't in the index at all, so every row the seek matches triggers a separate Key Lookup against the clustered index to fetch them.",
                CompliantSql: """
                    CREATE NONCLUSTERED INDEX IX_Orders_Status ON dbo.Orders(Status) INCLUDE (Total, Notes);
                    """,
                CompliantExplanation: "Total and Notes are now carried directly in the index as INCLUDE columns, so the seek alone satisfies the query - no Key Lookup remains in the plan."),
        ]);
}
