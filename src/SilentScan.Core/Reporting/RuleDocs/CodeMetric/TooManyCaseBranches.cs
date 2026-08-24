using SilentScan.Core.Predicates;
using SilentScan.Core.Reporting.Sarif;

namespace SilentScan.Core.Reporting.RuleDocs.CodeMetric;

internal static class TooManyCaseBranches
{
    public static string RuleId => SarifRuleCatalog.CodeMetricRuleId(CodeMetricFindingKind.TooManyCaseBranches);

    public static RuleDocContent Content { get; } = new(
        WhyItMatters: """
            A single CASE expression has more WHEN branches than the configured maximum. Purely a
            maintainability signal - no query result or execution plan is affected. A CASE with a
            very large number of branches is hard to scan for the one branch that matters, and
            usually signals a lookup table would represent the same mapping more maintainably.
            """,
        HowToFixIt: """
            Replace the long CASE expression with a JOIN against a lookup table holding the same
            mapping, so adding or changing a branch is a data change instead of a code change.
            """,
        Examples:
        [
            new RuleDocExample(
                Title: "A CASE expression enumerating many status codes",
                NoncompliantSql: """
                    SELECT OrderId,
                        CASE StatusCode
                            WHEN 1 THEN 'New' WHEN 2 THEN 'Processing' WHEN 3 THEN 'Shipped'
                            WHEN 4 THEN 'Delivered' WHEN 5 THEN 'Cancelled' WHEN 6 THEN 'Refunded'
                            WHEN 7 THEN 'OnHold' WHEN 8 THEN 'Returned'
                            ELSE 'Unknown'
                        END AS StatusText
                    FROM dbo.Orders;
                    """,
                NoncompliantExplanation: "Eight branches encode a status-code mapping directly in the query, past the configured maximum - the mapping can only be changed by editing this query wherever it's duplicated.",
                CompliantSql: """
                    SELECT o.OrderId, s.StatusText
                    FROM dbo.Orders o
                    JOIN dbo.OrderStatus s ON o.StatusCode = s.StatusCode;
                    """,
                CompliantExplanation: "The same mapping now lives in a lookup table, joined once instead of enumerated inline - adding a status is a data change, not a code change."),
        ]);
}
