using SilentScan.Core.Reporting.Sarif;

namespace SilentScan.Core.Reporting.RuleDocs.Correctness;

internal static class NotInNullableSubquery
{
    public static string RuleId => SarifRuleCatalog.NotInNullableSubqueryRuleId;

    public static RuleDocContent Content { get; } = new(
        WhyItMatters: """
            SQL doesn't work in ordinary two-valued (TRUE/FALSE) logic - every comparison against
            NULL, including inside an IN list, evaluates to a third truth value, UNKNOWN, rather
            than TRUE or FALSE. `x = NULL` is UNKNOWN, not FALSE, for any x. `NOT IN` is defined as
            a chain of `<>` comparisons ANDed together: `x NOT IN (1, 2, NULL)` means
            `x <> 1 AND x <> 2 AND x <> NULL`. The moment that list contains a NULL, its comparison
            term is UNKNOWN, and `AND` with UNKNOWN can never produce TRUE - `TRUE AND UNKNOWN` is
            UNKNOWN, not TRUE. So the entire expression collapses to UNKNOWN for every single value
            of x, including values that aren't anywhere in the list at all. A WHERE clause only
            keeps rows where the predicate is TRUE - UNKNOWN is treated exactly like FALSE for
            filtering purposes - so `WHERE x NOT IN (SELECT y FROM t)` silently returns zero rows
            the instant that subquery produces even one NULL y, no matter how many legitimately
            non-matching rows exist.

            Concretely: given `Orders(CustomerId)` with values (1, 2, 3) and a `Returns(CustomerId)`
            subquery that yields (2, NULL) - a `Returns` row with an unset CustomerId - the query
            `SELECT CustomerId FROM Orders WHERE CustomerId NOT IN (SELECT CustomerId FROM Returns)`
            is intended to return every customer who never returned anything: 1 and 3. In practice
            it returns nothing at all. For CustomerId = 1: `1 NOT IN (2, NULL)` is
            `1<>2 AND 1<>NULL` = `TRUE AND UNKNOWN` = UNKNOWN, dropped. Same for 3. The bug produces
            no error, no warning, and a perfectly plausible-looking empty result set - it's
            trivially mistaken for "there's genuinely no matching data" rather than a logic defect,
            which is exactly what makes it a silent correctness bug rather than a loud one.

            `NOT EXISTS` doesn't have this failure mode because it never compares values against
            the subquery's rows directly - it only asks whether a correlated row exists, and a row
            with a NULL CustomerId simply doesn't correlate-match, rather than poisoning the whole
            predicate to UNKNOWN. This is the standard, unconditionally NULL-safe rewrite for
            exactly this shape of anti-join.
            """,
        HowToFixIt: """
            Either add an unconditional `WHERE y IS NOT NULL` to the subquery, so a NULL row can
            never enter the NOT IN list in the first place, or rewrite the whole predicate as
            `NOT EXISTS (SELECT 1 FROM t WHERE t.y = outer.x)`, which is correlated rather than
            list-based and is NULL-safe by construction - a NULL y in the subquery simply fails to
            correlate, it never collapses the outer predicate to UNKNOWN. NOT EXISTS is generally
            the safer default to reach for even when the column is currently NOT NULL, since it
            can't regress silently if the column's nullability ever changes.
            """,
        Examples:
        [
            new RuleDocExample(
                Title: "One NULL in the subquery silently zeroes out the whole anti-join",
                NoncompliantSql: """
                    CREATE TABLE dbo.Orders
                    (
                        CustomerId INT NOT NULL PRIMARY KEY
                    );
                    CREATE TABLE dbo.Returns
                    (
                        ReturnId   INT NOT NULL PRIMARY KEY,
                        CustomerId INT NULL
                    );

                    SELECT CustomerId
                    FROM dbo.Orders
                    WHERE CustomerId NOT IN (SELECT CustomerId FROM dbo.Returns);
                    """,
                NoncompliantExplanation: "If dbo.Returns contains even one row whose CustomerId is NULL (e.g. an unresolved/anonymous return), the NOT IN list becomes (2, NULL)-shaped and every comparison collapses to UNKNOWN - the query silently returns zero rows instead of the customers who never returned anything.",
                CompliantSql: """
                    SELECT o.CustomerId
                    FROM dbo.Orders AS o
                    WHERE NOT EXISTS (
                        SELECT 1 FROM dbo.Returns AS r WHERE r.CustomerId = o.CustomerId
                    );
                    """,
                CompliantExplanation: "NOT EXISTS correlates row by row instead of building a NOT IN list - a NULL CustomerId in Returns simply never matches the correlation, so it can't drag the whole predicate to UNKNOWN."),
        ]);
}
