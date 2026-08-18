using SilentScan.Core.Reporting.Sarif;

namespace SilentScan.Core.Reporting.RuleDocs.Predicates;

internal static class LocalVariablePredicate
{
    public static string RuleId => SarifRuleCatalog.LocalVariablePredicateRuleId;

    public static RuleDocContent Content { get; } = new(
        WhyItMatters: """
            A predicate compared against a DECLARE'd local variable is fully sargable - the column
            still appears bare, an index on it can still be seeked, and the access path itself is
            not in question. What's different from comparing against a formal parameter is where
            the cardinality ESTIMATE comes from. When the optimizer compiles a plan for a predicate
            against a parameter, it can sniff the actual argument value the caller passed and build
            a value-specific estimate from the column's statistics histogram - "how many rows
            actually equal this value." A local variable's value is never visible to the optimizer
            at compile time: it's assigned by a separate statement (a SET or SELECT) that runs
            before the predicate, and the optimizer does not execute statements to discover what
            they'll assign. It has no choice but to fall back to a generic estimate built from the
            column's average density statistic - roughly "how many rows share the average distinct
            value across the whole column" - regardless of what the variable will actually hold.

            This is a meaningfully different failure mode from a catch-all OR @p IS NULL predicate:
            there the plan's overall SHAPE has to compromise across possible values. Here the shape
            is fine - it'll seek - but the estimated ROW COUNT feeding that seek is generic rather
            than value-specific. A generic average-density estimate can still steer the optimizer
            wrong further up the plan: a join order chosen because the local-variable predicate was
            estimated to return a small, average-ish number of rows when the actual value is highly
            skewed (matching far more or far fewer rows than average), or a memory grant sized for
            the generic estimate that's too small once the seek returns far more rows than
            expected, spilling to tempdb.

            Suppressed when the statement carries OPTION (RECOMPILE) or the procedure is WITH
            RECOMPILE - not because recompiling makes the variable's value visible at parse time in
            the general case, but because RECOMPILE causes the optimizer to defer estimation until
            the value the variable actually holds at execution can be used, giving the same
            value-specific estimate a sniffed parameter would get.
            """,
        HowToFixIt: """
            Add OPTION (RECOMPILE) to the statement, or WITH RECOMPILE to the procedure, so the
            cardinality estimate for the local-variable predicate is built from the variable's real
            runtime value instead of the column's generic average-density statistic. Where the
            variable's value is really just standing in for a caller-supplied value, converting it
            to a formal parameter of the procedure (so the optimizer can sniff it directly) is often
            a cleaner fix than reaching for RECOMPILE at all.
            """,
        Examples:
        [
            new RuleDocExample(
                Title: "A local variable's value is invisible to estimation",
                NoncompliantSql: """
                    CREATE TABLE dbo.Orders
                    (
                        OrderId    INT NOT NULL PRIMARY KEY,
                        CustomerId INT NOT NULL
                    );
                    CREATE INDEX IX_Orders_CustomerId ON dbo.Orders(CustomerId);

                    CREATE PROCEDURE dbo.FindOrdersForTopCustomer
                    AS
                    DECLARE @customerId INT = (SELECT TOP (1) CustomerId FROM dbo.Orders GROUP BY CustomerId ORDER BY COUNT(*) DESC);

                    SELECT OrderId
                    FROM dbo.Orders
                    WHERE CustomerId = @customerId;
                    """,
                NoncompliantExplanation: "The optimizer compiles the SELECT's plan without knowing what the earlier SET assigns to @customerId, so the seek against IX_Orders_CustomerId is real, but its estimated row count comes from the column's average density rather than from how many rows actually belong to the busiest customer - which is likely to be well above average.",
                CompliantSql: """
                    SELECT OrderId
                    FROM dbo.Orders
                    WHERE CustomerId = @customerId
                    OPTION (RECOMPILE);
                    """,
                CompliantExplanation: "OPTION (RECOMPILE) defers compilation until @customerId's actual value is known, letting the optimizer build a value-specific estimate from the histogram instead of the generic average-density fallback."),
        ]);
}
