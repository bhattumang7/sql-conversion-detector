using SilentScan.Core.Predicates;
using SilentScan.Core.Reporting.Sarif;

namespace SilentScan.Core.Reporting.RuleDocs.Predicates;

internal static class DynamicDataMaskingPredicateExposure
{
    public static string RuleId => SarifRuleCatalog.DynamicDataMaskingRuleId(DynamicDataMaskingFindingKind.PredicateExposure);

    public static RuleDocContent Content { get; } = new(
        WhyItMatters: """
            Dynamic Data Masking (`MASKED WITH (FUNCTION = ...)`) rewrites a masked column's
            displayed value for any caller who lacks the `UNMASK` permission - a `default()`-masked
            `INT` shows `0`, a masked `DATETIME` shows `1900-01-01`, `email()` shows an obfuscated
            address, and so on. That substitution happens only when the column's value is returned
            as output. It has no effect on how the engine evaluates a predicate, an `ORDER BY` key, or
            a `GROUP BY` key: those always compare the real, unmasked stored value, oracle-confirmed
            directly against the engine - `WHERE MaskedColumn = @guess` matches or fails to match
            based on the real value even though the same query's `SELECT MaskedColumn` would have
            displayed the sentinel, and `GROUP BY MaskedColumn` produces one group per distinct real
            value even though every group's displayed key looks identical.

            This turns masking into a boolean- or ranking-oracle: a caller with no `UNMASK` grant can
            still recover the real value of a masked column by probing it in a `WHERE`/`JOIN ON`/
            `HAVING` predicate and observing which rows come back, by counting `GROUP BY` groups, or
            by reading relative row order from `ORDER BY` - all without ever seeing the real value in
            an output column. Nothing about the query's shape looks unusual, and nothing errors.
            """,
        Examples:
        [
            new RuleDocExample(
                Title: "A WHERE-clause equality check silently exposes the real value",
                NoncompliantSql: """
                    CREATE TABLE dbo.Customers
                    (
                        CustomerId INT NOT NULL PRIMARY KEY,
                        Ssn        VARCHAR(11) MASKED WITH (FUNCTION = 'default()') NOT NULL
                    );

                    SELECT CustomerId FROM dbo.Customers WHERE Ssn = @candidateSsn;
                    """,
                NoncompliantExplanation: "A caller without UNMASK sees only the default() sentinel from any SELECT Ssn, but this predicate is evaluated against the real stored value - repeating the query with different @candidateSsn values lets that caller recover the real SSN one guess at a time, entirely through which rows come back.",
                CompliantSql: """
                    CREATE TABLE dbo.Customers
                    (
                        CustomerId INT NOT NULL PRIMARY KEY,
                        Ssn        VARCHAR(11) MASKED WITH (FUNCTION = 'default()') NOT NULL
                    );

                    SELECT CustomerId FROM dbo.Customers WHERE CustomerId = @customerId;
                    """,
                CompliantExplanation: "The predicate no longer touches the masked column at all, so a caller without UNMASK cannot use this query to probe the real Ssn value."),
        ]);
}
