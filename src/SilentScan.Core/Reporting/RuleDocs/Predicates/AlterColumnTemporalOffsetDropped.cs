using SilentScan.Core.Predicates;
using SilentScan.Core.Reporting.Sarif;

namespace SilentScan.Core.Reporting.RuleDocs.Predicates;

internal static class AlterColumnTemporalOffsetDropped
{
    public static string RuleId => SarifRuleCatalog.AlterColumnSafetyRuleId(AlterColumnSafetyKind.TemporalOffsetDropped);

    public static RuleDocContent Content { get; } = new(
        WhyItMatters: """
            DATETIMEOFFSET is the only built-in temporal type that stores a UTC offset alongside the
            date and time. ALTER TABLE ... ALTER COLUMN can retype a DATETIMEOFFSET column directly
            into DATETIME2, DATETIME, SMALLDATETIME, DATE, or TIME - none of which have room to
            represent an offset at all. The statement succeeds with no error, and it does not
            normalize the stored values to UTC first: it keeps every row's local date/time digits
            exactly as stored and simply discards the offset field.

            Two rows captured at the same real-world instant but in different offsets (say -05:00
            and +00:00) will read as two different-looking values once the column is narrowed, and
            every downstream comparison or ordering against the column is now comparing local clock
            readings with no shared frame of reference, not comparable instants.
            """,
        HowToFixIt: """
            Keep the column as DATETIMEOFFSET if the UTC offset is meaningful downstream - for
            cross-timezone data this is almost always the case. If normalizing to a specific offset
            (commonly UTC) before dropping it is genuinely intended, migrate the data explicitly
            with SWITCHOFFSET (or AT TIME ZONE) into a new column first, so the normalization is a
            visible decision rather than an invisible side effect of the ALTER COLUMN statement.
            """,
        Examples:
        [
            new RuleDocExample(
                Title: "Narrowing DATETIMEOFFSET to DATETIME2 silently drops the offset",
                NoncompliantSql: """
                    CREATE TABLE dbo.Events
                    (
                        EventId    INT            NOT NULL PRIMARY KEY,
                        OccurredAt DATETIMEOFFSET NOT NULL
                    );

                    ALTER TABLE dbo.Events ALTER COLUMN OccurredAt DATETIME2;
                    """,
                NoncompliantExplanation: "Every existing OccurredAt value's UTC offset is silently discarded - the local date/time digits are kept unchanged with no adjustment to UTC, and the ALTER COLUMN statement itself reports no error.",
                CompliantSql: """
                    CREATE TABLE dbo.Events
                    (
                        EventId    INT            NOT NULL PRIMARY KEY,
                        OccurredAt DATETIMEOFFSET NOT NULL
                    );
                    """,
                CompliantExplanation: "Leaving the column as DATETIMEOFFSET keeps every stored UTC offset intact."),
        ]);
}
