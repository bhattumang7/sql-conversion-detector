using SilentScan.Core.Reporting.Sarif;

namespace SilentScan.Core.Reporting.RuleDocs.Predicates;

internal static class FullTextPredicateInAggregate
{
    public static string RuleId => SarifRuleCatalog.FullTextPredicateInAggregateRuleId;

    public static RuleDocContent Content { get; } = new(
        WhyItMatters: """
            A `CONTAINS`/`FREETEXT` full-text predicate cannot appear inside a non-windowed
            aggregate function's own expression - most commonly reached via `SUM(CASE WHEN
            CONTAINS(...) THEN 1 ELSE 0 END)` or the equivalent with `COUNT`/`AVG`/`MIN`/`MAX`/
            `STRING_AGG` and the other standard aggregates. Confirmed directly against a real SQL
            Server instance: this fails to compile with Msg 30082 ("Full-text predicates cannot
            appear in an aggregate expression. Place the aggregate expression in a subquery."),
            with no GROUP BY required to trigger it - the restriction is purely about the full-text
            predicate being nested inside the aggregate's own expression tree, not about query
            shape. A full-text predicate anywhere else in the same query (a WHERE clause, a HAVING
            clause referencing the aggregate result, or inside a windowed aggregate that carries an
            `OVER` clause) is unaffected and compiles fine.
            """,
        HowToFixIt: """
            Move the full-text predicate out of the aggregate - filter the rows with CONTAINS/
            FREETEXT in a WHERE clause (or a derived table/subquery), then aggregate the
            already-filtered rows, instead of testing the predicate inside the aggregate's own
            expression.
            """,
        Examples:
        [
            new RuleDocExample(
                Title: "CONTAINS inside SUM's CASE expression never compiles",
                NoncompliantSql: """
                    SELECT SUM(CASE WHEN CONTAINS(Notes, 'urgent') THEN 1 ELSE 0 END)
                    FROM dbo.Ticket;
                    """,
                NoncompliantExplanation: "CONTAINS is nested inside SUM's own expression - this statement fails to compile with Msg 30082 every time it runs.",
                CompliantSql: """
                    SELECT COUNT(*)
                    FROM dbo.Ticket
                    WHERE CONTAINS(Notes, 'urgent');
                    """,
                CompliantExplanation: "CONTAINS filters the rows in a WHERE clause instead of appearing inside the aggregate - the statement compiles."),
        ]);
}
