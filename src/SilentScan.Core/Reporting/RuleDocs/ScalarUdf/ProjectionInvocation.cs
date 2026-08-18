using SilentScan.Core.Reporting.Sarif;

namespace SilentScan.Core.Reporting.RuleDocs.ScalarUdf;

internal static class ProjectionInvocation
{
    public static string RuleId => SarifRuleCatalog.ScalarUdfProjectionInvocationRuleId;

    public static RuleDocContent Content { get; } = new(
        WhyItMatters: """
            A scalar UDF called outside any predicate - in the SELECT list, an ORDER BY or GROUP BY
            expression, or a SET/variable assignment - doesn't cost the query its sargability, since
            there's no index seek being defeated here in the first place. What it still costs is
            per-row execution: unless the engine can prove the function meets SQL Server 2019+'s
            scalar UDF inlining requirements, every row the SELECT list (or ORDER BY/GROUP BY
            expression) touches triggers a separate invocation of the function as its own routine,
            with its own execution context switch, rather than being evaluated as part of the row's
            expression tree the way a built-in function or arithmetic operator would be. And exactly
            as with a predicate invocation, on any engine before 2019 - or on 2019+ when the function
            fails an inlining precondition - that per-row invocation forces the entire surrounding
            plan serial, so a query that would otherwise parallelize across a large table runs
            single-threaded purely because of one function call in its projection.

            Brent Ozar's benchmark of exactly this shape - a scalar UDF concatenating and
            NULL-coalescing two string columns in a SELECT list, nothing in a WHERE clause at all -
            is a useful gut check for how far this can go: what looks like a purely cosmetic
            formatting helper, called only in the output columns of a TOP 100 query, still measurably
            slows the query down, because "no predicate" doesn't mean "no per-row cost" - it only
            means the cost isn't compounding with a lost index seek.
            """,
        HowToFixIt: """
            Where the function body is a single expression, check it against SQL Server 2019+'s
            scalar UDF inlining requirements - a function that already qualifies may resolve this
            without any rewrite once the target engine is 2019 or later. Where it doesn't qualify, or
            the target engine is fixed below 2019, inline the function's expression directly into the
            SELECT list (or ORDER BY/GROUP BY expression, or assignment) in place of the function
            call.
            """,
        Examples:
        [
            new RuleDocExample(
                Title: "A scalar UDF formatting a display column, called only in the SELECT list",
                NoncompliantSql: """
                    CREATE FUNCTION dbo.FormatUsername(@DisplayName NVARCHAR(40), @Location NVARCHAR(100))
                    RETURNS NVARCHAR(200)
                    AS
                    BEGIN
                        DECLARE @Output NVARCHAR(200);
                        SET @Output = @DisplayName + N' from ' + COALESCE(@Location, N'Earth, probably');
                        RETURN @Output;
                    END;

                    SELECT TOP 100 dbo.FormatUsername(DisplayName, Location), Reputation
                    FROM dbo.Users
                    ORDER BY Reputation DESC;
                    """,
                NoncompliantExplanation: "FormatUsername's body has a local variable and an intermediate SET, so it doesn't qualify for 2019+ inlining - every one of the 100 output rows triggers a separate routine invocation purely to build a display string, with nothing in the WHERE clause even involved.",
                CompliantSql: """
                    SELECT TOP 100 DisplayName + N' from ' + COALESCE(Location, N'Earth, probably'), Reputation
                    FROM dbo.Users
                    ORDER BY Reputation DESC;
                    """,
                CompliantExplanation: "The formatting logic is inlined directly into the SELECT list - it's evaluated as an ordinary expression per row, with no separate function invocation and no risk of forcing the plan serial."),
        ]);
}
