using SilentScan.Core.Predicates;
using SilentScan.Core.Reporting.Sarif;

namespace SilentScan.Core.Reporting.RuleDocs.Tier1;

internal static class FunctionWrappedColumn
{
    public static string RuleId => SarifRuleCatalog.Tier1RuleId(SargabilityFindingKind.FunctionWrappedColumn);

    public static RuleDocContent Content { get; } = new(
        WhyItMatters: """
            SQL Server's optimizer can only use a B-tree index to seek directly to matching rows
            when it can compare the index key's own stored bytes against a value without first
            transforming them. A nonclustered index on a column stores that column's values in
            sorted order; the engine walks the B-tree by comparing the search value against the
            stored key, descending toward the match. That comparison only works if the stored key
            itself is what's being compared. The moment a predicate wraps the column in a
            function - YEAR(SomeDate), ISNULL(Age, 0), UPPER(Name), any of it - the expression the
            predicate actually evaluates is the function's OUTPUT, not the column's stored value.
            The index has no entries sorted by that output; it only has entries sorted by the raw
            column. So the optimizer can't seek through it at all for this predicate, and falls
            back to a full index or table scan, evaluating the function once per row to find
            matches.

            This is easy to miss because the code still looks like it's using an indexed column -
            the WHERE clause names the column directly. But the instant it's inside a function
            call, from the engine's point of view the predicate is against a computed value with
            no supporting index, and the query pays for a full scan on every execution regardless
            of table size. On a small table this is invisible; on a table with millions of rows it
            is the single most common cause of an unexplained slow query in production. The fix is
            almost always the same shape: move the transformation off the column and onto the
            comparison side instead, so the column itself is compared directly against something
            an index can seek to.
            """,
        HowToFixIt: """
            The general technique is called sargability (from "Search ARGument ABLE"): rewrite the
            predicate so the column appears bare on one side of the comparison, with any
            transformation pushed onto the constant/parameter side instead, which the optimizer
            can evaluate once at compile time rather than once per row.

            For a date-part function like YEAR(SomeDate) = 2018, the fix is a literal date range:
            SomeDate >= '20180101' AND SomeDate < '20190101'. Both bounds are plain comparisons
            against the raw column, so the optimizer can seek to the first matching row and scan
            forward only through the matching range.

            For ISNULL(Age, 0) = 0, rewrite as Age = 0 OR Age IS NULL - logically identical (a NULL
            Age or an Age of 0 both match), but now Age appears unwrapped on both branches, so each
            branch can seek independently.

            For CASE/COALESCE/IIF wrapping the column, the same principle applies: work out what
            set of raw-column conditions the wrapped expression is equivalent to, and write that
            directly instead of relying on the engine to evaluate the function per row.
            """,
        Examples:
        [
            new RuleDocExample(
                Title: "A date-part function forces a scan",
                NoncompliantSql: """
                    CREATE TABLE dbo.Orders
                    (
                        OrderId  INT      NOT NULL PRIMARY KEY,
                        OrderDate DATETIME NOT NULL
                    );
                    CREATE INDEX IX_Orders_OrderDate ON dbo.Orders(OrderDate);

                    SELECT OrderId
                    FROM dbo.Orders
                    WHERE YEAR(OrderDate) = 2018;
                    """,
                NoncompliantExplanation: "YEAR(OrderDate) must be evaluated per row before it can be compared to 2018 - the index on OrderDate is never consulted, and the engine scans every row in the table.",
                CompliantSql: """
                    SELECT OrderId
                    FROM dbo.Orders
                    WHERE OrderDate >= '20180101' AND OrderDate < '20190101';
                    """,
                CompliantExplanation: "OrderDate now appears bare on both sides of the range - the optimizer can seek IX_Orders_OrderDate directly to 2018-01-01 and scan forward only through matching rows."),
            new RuleDocExample(
                Title: "ISNULL wrapping the column",
                NoncompliantSql: """
                    CREATE TABLE dbo.Users
                    (
                        UserId INT NOT NULL PRIMARY KEY,
                        Age    INT NULL
                    );
                    CREATE INDEX IX_Users_Age ON dbo.Users(Age);

                    SELECT UserId
                    FROM dbo.Users
                    WHERE ISNULL(Age, 0) = 0;
                    """,
                NoncompliantExplanation: "ISNULL(Age, 0) produces a new value per row before the comparison runs, so IX_Users_Age can't be seeked.",
                CompliantSql: """
                    SELECT UserId
                    FROM dbo.Users
                    WHERE Age = 0 OR Age IS NULL;
                    """,
                CompliantExplanation: "Logically identical, but Age is now bare in both branches, so the optimizer can seek IX_Users_Age for each."),
        ]);
}
