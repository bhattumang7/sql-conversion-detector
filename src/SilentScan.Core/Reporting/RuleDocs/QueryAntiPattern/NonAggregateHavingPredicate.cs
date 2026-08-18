using SilentScan.Core.Predicates;
using SilentScan.Core.Reporting.Sarif;

namespace SilentScan.Core.Reporting.RuleDocs.QueryAntiPattern;

internal static class NonAggregateHavingPredicate
{
    public static string RuleId => SarifRuleCatalog.QueryAntiPatternRuleId(QueryAntiPatternFindingKind.NonAggregateHavingPredicate);

    public static RuleDocContent Content { get; } = new(
        WhyItMatters: """
            WHERE and HAVING are filters applied at different points in a grouped query's logical
            processing order: WHERE runs first, against individual rows, before grouping and
            aggregation happen; HAVING runs afterward, against the already-formed groups, which is
            exactly why HAVING is the only place a condition on an aggregate result (HAVING
            COUNT(*) > 5, HAVING SUM(Amount) > 1000) can be written at all - that value doesn't
            exist yet at the row level WHERE operates on. But when a HAVING condition mentions only
            columns that are also in the GROUP BY list, or plain literals, with no aggregate
            function involved anywhere in it, that condition doesn't actually need the grouped
            result to evaluate - it's testing the same thing WHERE could test on the raw rows,
            just later than it needs to.

            Because the condition is only on GROUP BY key values (which are single-valued per group
            by definition) rather than an aggregate, moving it to WHERE produces an identical final
            result: any row that would have caused its group to be excluded by the HAVING check is
            now filtered out before grouping, and every group HAVING would have kept is still
            formed and kept. The two placements are logically equivalent for this specific shape
            precisely because the condition never depended on the aggregation step.

            The cost of leaving it in HAVING is that the engine still aggregates every row of every
            group, including groups made entirely of rows that WHERE could have discarded before
            aggregation ever touched them - wasted grouping/aggregation work on rows that were
            never going to survive the filter anyway. On a query with a highly selective condition
            against a small fraction of the table, the difference between filtering before versus
            after grouping can be the difference between aggregating a handful of rows and
            aggregating the whole table.
            """,
        HowToFixIt: """
            Move the condition out of HAVING and into WHERE, leaving HAVING to carry only
            conditions that genuinely reference an aggregate result. This changes nothing about the
            final result set for a condition that doesn't touch an aggregate - it only moves the
            filtering earlier, before grouping/aggregation does its work instead of after.
            """,
        Examples:
        [
            new RuleDocExample(
                Title: "A GROUP BY key filter left in HAVING",
                NoncompliantSql: """
                    CREATE TABLE dbo.Sales (SaleId INT NOT NULL PRIMARY KEY, Region VARCHAR(20) NOT NULL, Amount DECIMAL(10,2) NOT NULL);

                    SELECT Region, SUM(Amount) AS TotalAmount
                    FROM dbo.Sales
                    GROUP BY Region
                    HAVING Region = 'West';
                    """,
                NoncompliantExplanation: "Region = 'West' names only the GROUP BY key, no aggregate - every row for every region still gets grouped and summed before HAVING discards all but the West group.",
                CompliantSql: """
                    SELECT Region, SUM(Amount) AS TotalAmount
                    FROM dbo.Sales
                    WHERE Region = 'West'
                    GROUP BY Region;
                    """,
                CompliantExplanation: "Rows outside the West region are filtered out before grouping ever runs, so only the rows that can possibly survive are aggregated - same result, less work."),
        ]);
}
