using SilentScan.Core.Reporting.Sarif;

namespace SilentScan.Core.Reporting.RuleDocs.WriteLoss;

internal static class TemporalOffsetDropped
{
    public static string RuleId => SarifRuleCatalog.WriteLossTemporalOffsetDroppedRuleId;

    public static RuleDocContent Content { get; } = new(
        WhyItMatters: """
            DATETIMEOFFSET is the only built-in temporal type that stores a UTC offset alongside the
            date and time - every other temporal type (DATETIME2, DATETIME, SMALLDATETIME, DATE,
            TIME) has no room to represent one at all. When a DATETIMEOFFSET value is assigned into
            one of those offset-unaware targets, SQL Server does not raise an error and it does not
            normalize the value to UTC first: it keeps the source's local date/time digits exactly
            as stored and simply discards the offset field. A value originally captured as
            2024-03-15T09:00:00 -05:00 becomes 2024-03-15T09:00:00 in the target - the same wall-clock
            digits, now with no record of which time zone they were ever relative to.

            This is a genuinely easy assumption to get backwards: it's natural to expect the engine
            to convert to a canonical instant (UTC) before dropping the offset, the way many
            application-level datetime libraries do. SQL Server does not do that here. Two source
            rows captured at the same real-world instant but in different offsets (say -05:00 and
            +00:00) will silently produce two different-looking values once copied into an
            offset-unaware target, and any downstream comparison or ordering against those values is
            comparing local clock readings with no shared frame of reference, not comparable
            instants.
            """,
        HowToFixIt: """
            Keep the value in a DATETIMEOFFSET-typed target if the UTC offset is meaningful
            downstream - for cross-timezone data this is almost always the case. If normalizing to a
            specific offset (commonly UTC) before dropping it is genuinely intended, do that
            explicitly with SWITCHOFFSET (or AT TIME ZONE) before assigning into the offset-unaware
            target, so the normalization is a visible decision in the query text rather than an
            invisible side effect of the target's declared type.
            """,
        Examples:
        [
            new RuleDocExample(
                Title: "A DATETIMEOFFSET value written into a DATETIME2 column",
                NoncompliantSql: """
                    CREATE TABLE dbo.Events
                    (
                        EventId    INT          NOT NULL PRIMARY KEY,
                        OccurredAt DATETIME2(3) NOT NULL
                    );

                    DECLARE @capturedAt DATETIMEOFFSET(3) = '2024-03-15T09:00:00.000 -05:00';

                    UPDATE dbo.Events
                    SET OccurredAt = @capturedAt
                    WHERE EventId = 1;
                    """,
                NoncompliantExplanation: "@capturedAt carries a -05:00 offset; assigning it into OccurredAt DATETIME2(3) silently drops the offset and keeps 2024-03-15T09:00:00.000 as-is, with no adjustment to UTC and no record of which time zone the value was originally relative to.",
                CompliantSql: """
                    CREATE TABLE dbo.Events
                    (
                        EventId    INT               NOT NULL PRIMARY KEY,
                        OccurredAt DATETIMEOFFSET(3) NOT NULL
                    );

                    DECLARE @capturedAt DATETIMEOFFSET(3) = '2024-03-15T09:00:00.000 -05:00';

                    UPDATE dbo.Events
                    SET OccurredAt = @capturedAt
                    WHERE EventId = 1;
                    """,
                CompliantExplanation: "OccurredAt is now DATETIMEOFFSET(3), the same type as the source value, so the UTC offset is preserved instead of being silently dropped."),
        ]);
}
