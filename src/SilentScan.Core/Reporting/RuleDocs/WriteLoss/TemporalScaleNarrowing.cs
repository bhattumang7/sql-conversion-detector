using SilentScan.Core.Reporting.Sarif;

namespace SilentScan.Core.Reporting.RuleDocs.WriteLoss;

internal static class TemporalScaleNarrowing
{
    public static string RuleId => SarifRuleCatalog.WriteLossTemporalScaleNarrowingRuleId;

    public static RuleDocContent Content { get; } = new(
        WhyItMatters: """
            TIME, DATETIME2, and DATETIMEOFFSET all carry a declared fractional-seconds scale from
            0 to 7 digits, the same way DECIMAL carries a declared scale. When a value with a wider
            fractional-seconds scale is assigned into a target declared with a narrower one - for
            example a DATETIME2(7) computation written into a DATETIME2(2) column or variable - SQL
            Server does not reject the assignment. It silently rounds the sub-second digits to the
            target's own scale and keeps the rest.

            This is the temporal sibling of the numeric scale-narrowing case: both sides are exact,
            well-defined temporal types, so there's no obvious representational mismatch to raise
            suspicion, but real digits of precision are discarded on every write past the target's
            declared scale. For anything measuring sub-second timing - event ordering, latency
            measurements, deduplication keys derived from a timestamp - the rounding can silently
            merge originally-distinct values or reorder events that were genuinely sequential at the
            source's own precision.
            """,
        HowToFixIt: """
            Widen the target's declared fractional-seconds scale to match (or exceed) the scale of
            the values being written into it, if the extra precision is actually meaningful
            downstream. If the extra digits genuinely don't matter, round the value explicitly to
            the target's own scale before assigning it, so the rounding is a decision visible in the
            query text rather than an invisible side effect of the target's declared type.
            """,
        Examples:
        [
            new RuleDocExample(
                Title: "A high-precision timestamp written into a low-precision column",
                NoncompliantSql: """
                    CREATE TABLE dbo.Events
                    (
                        EventId    INT           NOT NULL PRIMARY KEY,
                        OccurredAt DATETIME2(2)  NOT NULL
                    );

                    DECLARE @capturedAt DATETIME2(7) = SYSDATETIME();

                    UPDATE dbo.Events
                    SET OccurredAt = @capturedAt
                    WHERE EventId = 1;
                    """,
                NoncompliantExplanation: "@capturedAt carries 7 fractional-second digits; assigning it into OccurredAt DATETIME2(2) silently rounds it to two digits, discarding the extra sub-second precision the capture carried.",
                CompliantSql: """
                    CREATE TABLE dbo.Events
                    (
                        EventId    INT          NOT NULL PRIMARY KEY,
                        OccurredAt DATETIME2(7) NOT NULL
                    );

                    DECLARE @capturedAt DATETIME2(7) = SYSDATETIME();

                    UPDATE dbo.Events
                    SET OccurredAt = @capturedAt
                    WHERE EventId = 1;
                    """,
                CompliantExplanation: "OccurredAt now carries the same fractional-seconds scale as the captured value, so no sub-second digits are silently rounded away on write."),
        ]);
}
