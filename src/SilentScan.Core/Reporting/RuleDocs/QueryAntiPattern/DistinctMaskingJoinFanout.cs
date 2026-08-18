using SilentScan.Core.Predicates;
using SilentScan.Core.Reporting.Sarif;

namespace SilentScan.Core.Reporting.RuleDocs.QueryAntiPattern;

internal static class DistinctMaskingJoinFanout
{
    public static string RuleId => SarifRuleCatalog.QueryAntiPatternRuleId(QueryAntiPatternFindingKind.DistinctMaskingJoinFanout);

    public static RuleDocContent Content { get; } = new(
        WhyItMatters: """
            A join's row count is only guaranteed to match the driving side's row count when the
            columns it joins on are unique on the other side - join to a table on a column backed
            by a unique index or constraint, and each row from the driving side matches at most one
            row on the other side. Join on a column with no such uniqueness guarantee, and a single
            driving row can match several rows on the other side, so the joined result can contain
            more rows per driving-side entity than existed before the join - a fan-out. SELECT
            DISTINCT is sometimes reached for at exactly this point: the query returns rows that
            look duplicated, and DISTINCT makes the visible symptom go away by collapsing rows that
            are identical across every selected column.

            The trouble is that DISTINCT doesn't know why those rows looked duplicated - it can't
            distinguish "these two rows are true duplicates from a join fan-out I want collapsed"
            from "these two rows are legitimately different entities that just happen to have
            identical values in the columns I selected." When a join to a non-unique-backed table
            fans out and DISTINCT is relied on to clean up the result, any two genuinely different
            underlying rows that happen to agree on every selected column get silently collapsed
            into one, indistinguishable from the fan-out duplicates DISTINCT was meant to catch.
            This isn't a performance concern first - it's a correctness one: DISTINCT can quietly
            drop rows that were never duplicates at all, and nothing in the query's output signals
            that anything was dropped.

            The mechanism worth flagging here isn't "this query uses SELECT DISTINCT" in general -
            plenty of legitimate queries do - it's specifically a DISTINCT sitting downstream of a
            join whose own join columns on the joined table have no uniqueness guarantee backing
            them, which is the shape where DISTINCT is doing load-bearing work it was never
            designed to do safely.
            """,
        HowToFixIt: """
            There's no code-only rewrite that's correct in every case here, because the real
            question is why the join fans out in the first place. The two honest fixes are: add the
            uniqueness constraint that should have existed on the joined table's join columns, if
            the data model genuinely expects at most one matching row per join key (which also lets
            the optimizer verify and take advantage of that guarantee); or, if fan-out is actually
            expected (a genuine one-to-many relationship), restructure the query to handle that
            explicitly - aggregate the many side down to one row per key before joining, or make
            the one-to-many relationship visible in the result shape - rather than joining first and
            using DISTINCT to paper over however many rows the fan-out happens to produce.
            """,
        Examples:
        [
            new RuleDocExample(
                Title: "DISTINCT masking a join fan-out with no unique index on the joined side",
                NoncompliantSql: """
                    CREATE TABLE dbo.Customers (CustomerId INT NOT NULL PRIMARY KEY, Name VARCHAR(100) NOT NULL);
                    CREATE TABLE dbo.CustomerPhones (PhoneId INT NOT NULL PRIMARY KEY, CustomerId INT NOT NULL, Phone VARCHAR(20) NOT NULL);
                    -- No unique index/constraint on CustomerPhones.CustomerId: a customer can have several phone rows.

                    SELECT DISTINCT c.CustomerId, c.Name
                    FROM dbo.Customers AS c
                    JOIN dbo.CustomerPhones AS p ON p.CustomerId = c.CustomerId;
                    """,
                NoncompliantExplanation: "Each customer with more than one phone row fans out into multiple joined rows; DISTINCT collapses them back down by CustomerId/Name, but it would collapse them the same way even if two different customers coincidentally shared a Name - nothing here actually distinguishes the two cases."),
        ]);
}
