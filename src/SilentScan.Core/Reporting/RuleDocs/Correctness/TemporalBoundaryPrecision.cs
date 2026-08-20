using SilentScan.Core.Reporting.Sarif;

namespace SilentScan.Core.Reporting.RuleDocs.Correctness;

internal static class TemporalBoundaryPrecision
{
    public static string RuleId => SarifRuleCatalog.TemporalBoundaryPrecisionRuleId;

    public static RuleDocContent Content { get; } = new(
        WhyItMatters: """
            `BETWEEN` on a date/time column is inclusive on both ends, which makes writing an
            "end of period" upper bound as a literal deceptively easy to get wrong: the literal can
            only ever carry as many fractional-second digits as someone typed, but the column's own
            declared precision (TIME/DATETIME2/DATETIMEOFFSET can all go to 7 fractional digits) may
            carry more. `WHERE OccurredAt BETWEEN '2024-01-01' AND '2024-12-31 23:59:59.997'` against
            a `DATETIME2(7)` column looks like it covers the whole year, but a row stamped
            `2024-12-31 23:59:59.9999999` - a value later in the same second than the literal's own
            three fractional digits reach - compares greater than the upper bound and is silently
            excluded. Nothing about the query fails or warns; it simply returns one row fewer than
            the author intended, for exactly the rows that occurred in the precision gap between the
            literal's own digits and the column's real declared scale.

            This is the well-documented "bad habit" of using BETWEEN for a date range (the canonical
            write-up traces back to Aaron Bertrand's widely-cited piece on exactly this bug), reported
            here as a correctness defect rather than a sargability one: the predicate can still seek
            an index perfectly well, but the row set it returns is wrong. It's classified alongside
            this tool's other silent-data-loss findings for that reason - the query runs, returns a
            plausible result, and nothing about the output signals that specific rows were dropped.
            """,
        HowToFixIt: """
            Rewrite the predicate as `>= start AND < (start of the next period)` instead of
            `BETWEEN start AND end` - a half-open range has no precision gap to fall into, regardless
            of the column's declared fractional-second scale.
            """,
        Examples:
        [
            new RuleDocExample(
                Title: "A year-end BETWEEN literal narrower than the column's own precision",
                NoncompliantSql: """
                    CREATE TABLE dbo.Events
                    (
                        EventId    INT NOT NULL PRIMARY KEY,
                        OccurredAt DATETIME2(7) NOT NULL
                    );

                    SELECT EventId
                    FROM dbo.Events
                    WHERE OccurredAt BETWEEN '2024-01-01' AND '2024-12-31 23:59:59.997';
                    """,
                NoncompliantExplanation: "OccurredAt is DATETIME2(7), but the upper bound literal only carries 3 fractional digits - a row stamped 2024-12-31 23:59:59.9999999 falls after the literal and is silently excluded, even though it's plainly within 2024.",
                CompliantSql: """
                    SELECT EventId
                    FROM dbo.Events
                    WHERE OccurredAt >= '2024-01-01' AND OccurredAt < '2025-01-01';
                    """,
                CompliantExplanation: "The half-open range has no upper-bound precision to fall short of - every value in 2024, regardless of its fractional-second digits, satisfies OccurredAt < '2025-01-01'."),
        ]);
}
