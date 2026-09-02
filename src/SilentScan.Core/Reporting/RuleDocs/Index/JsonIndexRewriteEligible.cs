using SilentScan.Core.Reporting.Sarif;

namespace SilentScan.Core.Reporting.RuleDocs.Index;

internal static class JsonIndexRewriteEligible
{
    public static string RuleId => SarifRuleCatalog.JsonIndexRewriteEligibleRuleId;

    public static RuleDocContent Content { get; } = new(
        WhyItMatters: """
            SQL Server 2025 adds a native `JSON` data type and `CREATE JSON INDEX`, backed by an
            internal table the optimizer can seek. But that index is only reachable through
            `JSON_CONTAINS(column, value, path)` - it does not make `JSON_VALUE(column, path) = value`
            itself seekable, because `JSON_VALUE` wraps the column in a function call like any other
            non-sargable predicate. Oracle-confirmed directly against a real 2025 engine: on a JSON
            column with a JSON index and thousands of rows, `WHERE JSON_VALUE(j,'$.a') = '2500'`
            still produces a `Clustered Index Scan`, while `WHERE JSON_CONTAINS(j, 2500, '$.a') = 1`
            against the identical table produces a `Nested Loops` plan with `Clustered Index Seek`
            operators against the JSON index by name.

            This rule fires when an equality predicate compares `JSON_VALUE(column, path)` against a
            value and that column has a JSON index - the predicate as written stays non-sargable (the
            general function-wrapped-column rule already flags that), but the JSON index makes a
            specific rewrite available that a plain function-wrapped-column finding can't suggest.
            """,
        HowToFixIt: """
            Rewrite `JSON_VALUE(column, path) = value` as `JSON_CONTAINS(column, value, path) = 1` so
            the predicate seeks the JSON index instead of scanning the table.
            """,
        Examples:
        [
            new RuleDocExample(
                Title: "An equality comparison against JSON_VALUE on a JSON-indexed column",
                NoncompliantSql: """
                    CREATE TABLE dbo.Orders
                    (
                        Id      INT NOT NULL PRIMARY KEY,
                        Payload JSON NOT NULL
                    );
                    CREATE JSON INDEX IX_Orders_Payload ON dbo.Orders(Payload);

                    SELECT Id
                    FROM dbo.Orders
                    WHERE JSON_VALUE(Payload, '$.status') = 'shipped';
                    """,
                NoncompliantExplanation: "Payload has a JSON index, but JSON_VALUE(Payload, '$.status') wraps the column in a function call, so the comparison still scans dbo.Orders.",
                CompliantSql: """
                    SELECT Id
                    FROM dbo.Orders
                    WHERE JSON_CONTAINS(Payload, 'shipped', '$.status') = 1;
                    """,
                CompliantExplanation: "JSON_CONTAINS is the form the optimizer can seek the JSON index with - the comparison is no longer a function wrapped around the column."),
        ]);
}
