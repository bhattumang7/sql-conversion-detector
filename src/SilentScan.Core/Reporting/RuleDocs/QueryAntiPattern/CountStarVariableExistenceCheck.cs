using SilentScan.Core.Predicates;
using SilentScan.Core.Reporting.Sarif;

namespace SilentScan.Core.Reporting.RuleDocs.QueryAntiPattern;

internal static class CountStarVariableExistenceCheck
{
    public static string RuleId => SarifRuleCatalog.QueryAntiPatternRuleId(QueryAntiPatternFindingKind.CountStarVariableExistenceCheck);

    public static RuleDocContent Content { get; } = new(
        WhyItMatters: """
            SELECT COUNT(*) FROM ... WHERE ... assigned into a variable, followed by a separate
            IF @count > 0 (or = 0) check, forces the engine to compute the true, exact count of
            every matching row before either statement can proceed - there is no way for the
            optimizer to know, from the assignment statement alone, that the caller only cares
            whether the count is zero or nonzero, because that intent lives in the next statement,
            not this one. A full aggregation has to touch every matching row (or, at best, every
            row an index can be scanned or seeked across for the predicate) even when the first
            matching row would have already answered the only question actually being asked.

            This scan's oracle confirms the shape mechanically: the assignment (`SET @x =
            (SELECT COUNT(*) ...)` or `SELECT @x = COUNT(*) ...`) is immediately followed by an IF
            comparing that same variable only against the literal 0. Contrast this with the inline
            scalar-subquery form written directly in the IF - `IF (SELECT COUNT(*) FROM ... WHERE
            ...) > 0` - which the optimizer already recognizes and rewrites: because the comparison
            and the aggregation are visible to the optimizer in the same statement, it can transform
            the count into a semi-join / EXISTS-equivalent plan that stops at the first qualifying
            row instead of counting them all. That rewrite is only possible when the optimizer can
            see, at compile time, that the count feeds a > 0 or = 0 comparison and nothing else -
            which it can't when the count is stashed in a variable first and read back in a later,
            separate statement.

            The two forms are logically identical - both answer "does at least one row match?" -
            but only one of them lets the engine stop early. On a predicate matching a handful of
            rows out of a small table, the difference is negligible; on a predicate that matches
            (or fails to match) against millions of candidate rows, forcing the full count turns an
            existence check that should be near-instant into a scan of the whole matching set.
            """,
        HowToFixIt: """
            Use IF EXISTS (SELECT 1 FROM ... WHERE ...) in place of the count-then-compare pair -
            EXISTS is a pure existence test by construction and the optimizer stops at the first
            qualifying row. Where the count value itself is genuinely needed later (not just an
            existence check), keep the assignment but stop treating it as an existence check: the
            fix here is specifically for the case where the only thing ever done with the count is
            compare it to zero.
            """,
        Examples:
        [
            new RuleDocExample(
                Title: "COUNT(*) assigned to a variable, then compared to zero",
                NoncompliantSql: """
                    CREATE TABLE dbo.Orders (OrderId INT NOT NULL PRIMARY KEY, CustomerId INT NOT NULL);

                    DECLARE @OpenOrderCount INT;

                    SELECT @OpenOrderCount = COUNT(*)
                    FROM dbo.Orders
                    WHERE CustomerId = 42;

                    IF @OpenOrderCount > 0
                    BEGIN
                        PRINT 'Customer has orders.';
                    END;
                    """,
                NoncompliantExplanation: "The assignment statement has no visibility into how @OpenOrderCount will be used - the optimizer must compute the exact count of every matching row, even though the IF that follows only cares whether that count is nonzero.",
                CompliantSql: """
                    IF EXISTS (SELECT 1 FROM dbo.Orders WHERE CustomerId = 42)
                    BEGIN
                        PRINT 'Customer has orders.';
                    END;
                    """,
                CompliantExplanation: "EXISTS is a pure existence check the optimizer can short-circuit on the first qualifying row, instead of aggregating the full matching set."),
        ]);
}
