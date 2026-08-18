using SilentScan.Core.Reporting.Sarif;

namespace SilentScan.Core.Reporting.RuleDocs.ScalarUdf;

internal static class PredicateInvocation
{
    public static string RuleId => SarifRuleCatalog.ScalarUdfPredicateInvocationRuleId;

    public static RuleDocContent Content { get; } = new(
        WhyItMatters: """
            A scalar UDF called inside a `WHERE`, `JOIN ... ON`, `HAVING`, or `MERGE ... ON`
            predicate carries two separate costs, and it's worth keeping them apart. The first is
            sargability: like any function wrapped around a column, the predicate now evaluates a
            computed value rather than the column's raw stored bytes, so an index on that column
            can't be seeked. The second, and the one this rule is specifically catalog-proving, is
            per-row execution cost: unless the engine can prove the function is safe to inline (T-SQL
            scalar UDF inlining, available since SQL Server 2019, requires the function body to meet
            a specific list of conditions - a single RETURN, no side effects, no reference to
            non-deterministic built-ins, and more), the function executes as its own separately
            invoked routine once per row the predicate is evaluated against, with its own execution
            context switch each time. On any version before 2019, or on 2019+ when the function
            fails an inlining precondition, that per-row invocation additionally forces the entire
            surrounding query plan serial - SQL Server cannot parallelize a plan that contains a
            non-inlined scalar UDF call, so a query that would otherwise get a parallel plan on a
            large table runs single-threaded end to end because of one function call in a predicate.

            This is deliberately reported independently from a syntactic function-wrapped-column
            finding on the same predicate: the two are different claims backed by different
            evidence. The sargability rule fires from parse-level pattern matching against any
            function; this rule fires from the engine's own catalog-recorded inlining status for
            this specific function, and the claim it's making - per-row cost and, conditionally,
            forced-serial execution - is a distinct, stronger claim that a syntactically-similar but
            genuinely inlineable function wouldn't earn.
            """,
        HowToFixIt: """
            Where the function body is a single expression (`RETURN <expr>` with no branching,
            temp state, or non-deterministic calls), verify it against SQL Server 2019+'s scalar UDF
            inlining requirements - if it already qualifies, upgrading the target engine to 2019+ may
            resolve this without a rewrite. Where it doesn't qualify, or the engine version is fixed
            below 2019, inline the function's logic directly into the predicate as an ordinary
            expression, or rewrite it as an inline TVF joined via `CROSS APPLY` so its logic is
            expanded into the query plan instead of invoked as a separate per-row routine.
            """,
        Examples:
        [
            new RuleDocExample(
                Title: "A scalar UDF driving a WHERE predicate",
                NoncompliantSql: """
                    CREATE FUNCTION dbo.discount_price(@price DECIMAL(12,2), @discount DECIMAL(12,2))
                    RETURNS DECIMAL(12,2)
                    AS
                    BEGIN
                        RETURN @price * (1 - @discount);
                    END;

                    CREATE TABLE dbo.LineItem
                    (
                        LineItemId    INT           NOT NULL PRIMARY KEY,
                        ExtendedPrice DECIMAL(12,2) NOT NULL,
                        Discount      DECIMAL(12,2) NOT NULL
                    );

                    SELECT LineItemId
                    FROM dbo.LineItem
                    WHERE dbo.discount_price(ExtendedPrice, Discount) > 100.00;
                    """,
                NoncompliantExplanation: "On an engine/catalog state where discount_price isn't inlined, every row's predicate evaluation is a separate routine invocation, and the whole plan is forced serial - on a 10GB TPC-H-scale LineItem table, Microsoft's own measurement of this exact function went from 29 minutes down to 1.6 seconds once it inlined.",
                CompliantSql: """
                    SELECT LineItemId
                    FROM dbo.LineItem
                    WHERE ExtendedPrice * (1 - Discount) > 100.00;
                    """,
                CompliantExplanation: "The expression is inlined directly into the predicate - no per-row function invocation, no forced-serial plan, and the predicate is sargable against ExtendedPrice/Discount directly."),
        ]);
}
