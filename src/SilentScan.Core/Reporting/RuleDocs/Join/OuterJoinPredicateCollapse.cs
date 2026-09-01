using SilentScan.Core.Reporting.Sarif;

namespace SilentScan.Core.Reporting.RuleDocs.Join;

internal static class OuterJoinPredicateCollapse
{
    public static string RuleId => SarifRuleCatalog.OuterJoinPredicateCollapseRuleId;

    public static RuleDocContent Content { get; } = new(
        WhyItMatters: """
            An OUTER JOIN's whole reason for existing is to keep rows from one side even when no
            match exists on the other - every column from that null-supplying side comes back as
            NULL on those unmatched rows. SQL Server evaluates a query's WHERE clause after the
            join, against the join's own output rows. A comparison, LIKE, IN, or BETWEEN predicate
            against a null-supplying column is three-valued logic: when that column is NULL, the
            comparison evaluates to NULL rather than TRUE, and WHERE discards NULL just like FALSE.
            The unmatched rows the OUTER JOIN was written to preserve are filtered straight back
            out - the query still parses and runs as an OUTER JOIN, but its actual row set is
            identical to what an INNER JOIN would have produced, silently defeating the join type
            the author chose.

            The fix depends on what was intended. If the predicate is meant to filter the joined
            rows - "only orders with an active shipment" - an INNER JOIN says that plainly and an
            OUTER JOIN that behaves like one is just misleading. If the predicate was meant to
            optionally narrow the outer side while still keeping unmatched rows, it needs an
            explicit `OR <column> IS NULL` guard, or it belongs in the join's own ON clause instead
            of the WHERE clause - ON-clause predicates on the null-supplying side do not filter out
            the outer row, they only decide whether that particular match counts.

            This finding is scoped to the WHERE clause only: an unsatisfiable predicate written into
            a subsequent join's own ON clause does not eliminate an earlier OUTER JOIN's unmatched
            rows, since ON failing just means no match for that specific join, not exclusion of the
            row already produced upstream. It is also scoped to predicates that are not themselves
            wrapped in an OR at the top level of their AND-conjunct - an OR can supply an escape
            hatch (a condition on an unrelated column, or an explicit IS NULL guard) that keeps the
            unmatched row alive through a path this rule does not attempt to prove out, so those are
            left unflagged rather than risk a false positive.
            """,
        HowToFixIt: """
            If the predicate is meant to require a match, rewrite the join as an INNER JOIN so the
            code says what it does. If unmatched rows should be preserved, either move the predicate
            into the join's own ON clause, or add an explicit `OR <column> IS NULL` guard to the
            WHERE clause so a NULL from an unmatched row survives the filter.
            """,
        Examples:
        [
            new RuleDocExample(
                Title: "A WHERE predicate on the null-supplying side of a LEFT JOIN",
                NoncompliantSql: """
                    SELECT c.CustomerId, o.OrderId
                    FROM dbo.Customers AS c
                    LEFT JOIN dbo.Orders AS o ON o.CustomerId = c.CustomerId
                    WHERE o.Status = 'Open';
                    """,
                NoncompliantExplanation: "Every customer with no orders has o.Status = NULL, and NULL = 'Open' is neither TRUE nor FALSE, so WHERE drops those rows - the query returns exactly what an INNER JOIN would, even though it says LEFT JOIN.",
                CompliantSql: """
                    SELECT c.CustomerId, o.OrderId
                    FROM dbo.Customers AS c
                    LEFT JOIN dbo.Orders AS o
                        ON o.CustomerId = c.CustomerId AND o.Status = 'Open';
                    """,
                CompliantExplanation: "Moving the filter into the join's own ON clause keeps a customer row even when they have no open order - a failed ON match still leaves the LEFT JOIN's unmatched row in the result, unlike a failed WHERE predicate."),
        ]);
}
