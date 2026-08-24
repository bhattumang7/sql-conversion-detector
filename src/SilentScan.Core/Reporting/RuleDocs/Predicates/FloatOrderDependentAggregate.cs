using SilentScan.Core.Reporting.Sarif;

namespace SilentScan.Core.Reporting.RuleDocs.Predicates;

internal static class FloatOrderDependentAggregate
{
    public static string RuleId => SarifRuleCatalog.FloatOrderDependentAggregateRuleId;

    public static RuleDocContent Content { get; } = new(
        WhyItMatters: """
            `SUM`, `AVG`, `VAR`, `VARP`, `STDEV`, and `STDEVP` accumulate their result by repeatedly
            adding IEEE-754 binary floating-point values together, and binary floating-point addition
            is not associative: `(a + b) + c` and `a + (b + c)` can produce different bit patterns for
            the same three inputs, because each intermediate sum is rounded to the nearest
            representable value before the next addition happens. The engine gives no guarantee about
            the order in which rows are fed into one of these aggregates - a serial plan may accumulate
            them in storage order, a parallel plan splits the rows across threads and combines their
            partial sums in whatever order the threads finish, and neither order is fixed across runs,
            degrees of parallelism, or SQL Server versions. The result: the identical aggregate query,
            run twice against identical unchanged data, can return a different bit pattern purely
            because the optimizer chose a different plan shape or scheduled the parallel threads
            differently - with no error, no warning, and no code change to point at.

            `MIN`, `MAX`, and `COUNT`/`COUNT_BIG` are not affected and are not flagged by this rule:
            `MIN`/`MAX` only ever compare values against each other rather than combining them
            arithmetically, and `COUNT`/`COUNT_BIG` never touch the aggregated column's value at all -
            none of the three has an accumulation order for floating-point rounding error to depend on.
            """,
        Examples:
        [
            new RuleDocExample(
                Title: "SUM over a FLOAT column can return a different result across plan shapes",
                NoncompliantSql: """
                    CREATE TABLE dbo.SensorReadings
                    (
                        ReadingId  INT   NOT NULL PRIMARY KEY,
                        Value      FLOAT NOT NULL
                    );

                    SELECT SUM(Value) AS Total
                    FROM dbo.SensorReadings;
                    """,
                NoncompliantExplanation: "SUM accumulates Value in whatever order the chosen plan happens to process rows - a serial plan and a parallel plan (or two parallel plans with a different degree of parallelism) can legitimately combine the same rows in a different order and return a different bit pattern for Total, with no error raised either time.",
                CompliantSql: """
                    CREATE TABLE dbo.SensorReadings
                    (
                        ReadingId  INT             NOT NULL PRIMARY KEY,
                        Value      DECIMAL(18, 6)  NOT NULL
                    );

                    SELECT SUM(Value) AS Total
                    FROM dbo.SensorReadings;
                    """,
                CompliantExplanation: "DECIMAL is an exact base-10 type, so summing it produces the same exact result regardless of the order the plan happens to combine rows in. Where FLOAT is genuinely required, treat SUM/AVG/VAR/VARP/STDEV/STDEVP results over it as reproducible only up to floating-point rounding error, not bit-for-bit stable across runs."),
        ]);
}
