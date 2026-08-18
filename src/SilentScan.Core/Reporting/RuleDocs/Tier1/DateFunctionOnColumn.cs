using SilentScan.Core.Predicates;
using SilentScan.Core.Reporting.Sarif;

namespace SilentScan.Core.Reporting.RuleDocs.Tier1;

internal static class DateFunctionOnColumn
{
    public static string RuleId => SarifRuleCatalog.Tier1RuleId(SargabilityFindingKind.DateFunctionOnColumn);

    public static RuleDocContent Content { get; } = new(
        WhyItMatters: """
            YEAR(OrderDate) = 2018, DATEDIFF(day, OrderDate, GETDATE()) < 30, MONTH(OrderDate) = 6 -
            date-part extraction is one of the most common places this rule fires, because "find
            everything from this year/month/the last N days" is such a routine business question.
            Every one of these wraps the date column in a function whose output - a plain integer
            year, a day count, a month number - is what actually gets compared, not the date value
            itself. The index on the date column is sorted by date, not by "what year this date
            falls in", so it can't be seeked for a predicate phrased that way, and the engine falls
            back to evaluating the function per row across a full scan.

            The good news is that almost every date-part predicate has an exactly equivalent
            literal date-range rewrite, because a date-part extraction is really asking "does this
            date fall within a certain period" - which a plain >= / < range against the raw column
            answers identically, and ranges are exactly what a B-tree index seeks efficiently.
            """,
        HowToFixIt: """
            Convert the date-part condition into an equivalent range against the bare column.
            YEAR(OrderDate) = 2018 becomes OrderDate >= '20180101' AND OrderDate < '20190101'.
            DATEDIFF(day, OrderDate, GETDATE()) < 30 becomes OrderDate > DATEADD(day, -30,
            GETDATE()) - note GETDATE() here is evaluated once, not per row, and OrderDate stays
            bare. The general pattern: identify the period the date-part comparison is really
            asking about, then write that period as a >= start AND < end range directly against
            the column.
            """,
        Examples:
        [
            new RuleDocExample(
                Title: "YEAR() extraction forces a scan",
                NoncompliantSql: """
                    CREATE TABLE dbo.Orders
                    (
                        OrderId   INT      NOT NULL PRIMARY KEY,
                        OrderDate DATETIME NOT NULL
                    );
                    CREATE INDEX IX_Orders_OrderDate ON dbo.Orders(OrderDate);

                    SELECT OrderId
                    FROM dbo.Orders
                    WHERE YEAR(OrderDate) = 2018;
                    """,
                NoncompliantExplanation: "YEAR(OrderDate) is computed per row before the comparison, so IX_Orders_OrderDate can't be seeked.",
                CompliantSql: """
                    SELECT OrderId
                    FROM dbo.Orders
                    WHERE OrderDate >= '20180101' AND OrderDate < '20190101';
                    """,
                CompliantExplanation: "The same rows, expressed as a literal range against the bare column - the optimizer seeks to 2018-01-01 and scans forward only through the matching range."),
            new RuleDocExample(
                Title: "DATEDIFF against \"now\" forces a scan",
                NoncompliantSql: """
                    SELECT OrderId
                    FROM dbo.Orders
                    WHERE DATEDIFF(day, OrderDate, GETDATE()) < 30;
                    """,
                NoncompliantExplanation: "DATEDIFF(...) is computed per row, wrapping OrderDate and defeating the seek.",
                CompliantSql: """
                    SELECT OrderId
                    FROM dbo.Orders
                    WHERE OrderDate > DATEADD(day, -30, GETDATE());
                    """,
                CompliantExplanation: "GETDATE() and the 30-day offset are evaluated once at compile/execution time, not per row - OrderDate is compared bare against a single computed cutoff, and the optimizer can seek."),
        ]);
}
