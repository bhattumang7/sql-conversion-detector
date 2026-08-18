using SilentScan.Core.Reporting.Sarif;

namespace SilentScan.Core.Reporting.RuleDocs.Predicates;

internal static class FilteredIndexParameterMismatch
{
    public static string RuleId => SarifRuleCatalog.FilteredIndexParameterMismatchRuleId;

    public static RuleDocContent Content { get; } = new(
        WhyItMatters: """
            A filtered index carries its own WHERE clause, evaluated once at index-build/maintenance
            time, that restricts which rows the index physically contains - CREATE INDEX
            IX_Orders_Open ON Orders(CustomerId) WHERE Status = 'Open' stores only the Open rows.
            For the optimizer to use that index for a query, it has to prove the query's own
            predicate can only ever need rows the filtered index actually contains - and it proves
            that with simple, syntactic matching against the index's filter expression, not general
            logical reasoning. That matching succeeds when the query restates the same condition
            using a LITERAL value: WHERE Status = 'Open' matches the index's filter directly, and
            the optimizer can use the index. It does not succeed when the query instead compares
            against a parameter or local variable: WHERE Status = @status is, from the optimizer's
            point of view, a predicate whose value isn't known until runtime, and it cannot prove -
            purely by matching expressions at compile time - that @status will always equal 'Open'
            for every future execution, even if in practice it always does.

            So a predicate on a column that carries a filtered index whose filter is a literal
            restatement of that exact comparison can never use that index when written against a
            parameter or variable, no matter what value is actually passed at runtime - not because
            the value happens to be wrong on some call, but because the query shape itself doesn't
            qualify for the match. The optimizer falls back to whatever other access path is
            available (a different, non-filtered index, or a scan), permanently, for every call.

            This is worth calling out explicitly because it looks exactly like the two prior
            findings (catch-all predicates and local-variable predicates), both of which are fixed
            by OPTION (RECOMPILE) - and this one is not. RECOMPILE only helps when the problem is
            that the optimizer needs to see the real runtime value to build a better plan or a
            better estimate, which it does for both of those. Here the plan is not being built with
            a stale or generic assumption; the filtered index is structurally unusable for a
            parameterized predicate at compile time regardless of when compilation happens, so
            recompiling changes nothing.
            """,
        Examples:
        [
            new RuleDocExample(
                Title: "A filtered index that a parameterized predicate can never use",
                NoncompliantSql: """
                    CREATE TABLE dbo.Orders
                    (
                        OrderId    INT         NOT NULL PRIMARY KEY,
                        CustomerId INT         NOT NULL,
                        Status     VARCHAR(10) NOT NULL
                    );
                    CREATE INDEX IX_Orders_Open ON dbo.Orders(CustomerId) WHERE Status = 'Open';

                    CREATE PROCEDURE dbo.FindOrdersByStatus (@status VARCHAR(10))
                    AS
                    SELECT OrderId
                    FROM dbo.Orders
                    WHERE CustomerId = 42 AND Status = @status;
                    """,
                NoncompliantExplanation: "IX_Orders_Open's own filter (Status = 'Open') can only be matched against a query that restates it with the literal 'Open' - comparing Status against @status can never satisfy that match at compile time, so this query can never use the index, even on a call where @status happens to be 'Open'. Adding OPTION (RECOMPILE) would not change this outcome.",
                CompliantSql: """
                    SELECT OrderId
                    FROM dbo.Orders
                    WHERE CustomerId = 42 AND Status = 'Open';
                    """,
                CompliantExplanation: "With the literal 'Open' written directly into the query, the predicate now matches the filtered index's own filter expression and the optimizer can use IX_Orders_Open - but only because this query is now hardcoded to that one status."),
        ]);
}
