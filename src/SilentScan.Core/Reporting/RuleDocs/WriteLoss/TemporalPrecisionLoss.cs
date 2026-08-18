using SilentScan.Core.Reporting.Sarif;

namespace SilentScan.Core.Reporting.RuleDocs.WriteLoss;

internal static class TemporalPrecisionLoss
{
    public static string RuleId => SarifRuleCatalog.WriteLossTemporalPrecisionLossRuleId;

    public static RuleDocContent Content { get; } = new(
        WhyItMatters: """
            DATE stores only a calendar date - year, month, day - with no time-of-day component at
            all. DATETIME, DATETIME2, SMALLDATETIME, and DATETIMEOFFSET all store a time component
            alongside the date (and DATETIMEOFFSET additionally stores a UTC offset). When an
            INSERT or UPDATE assigns one of these richer temporal values into a DATE target, SQL
            Server performs an implicit narrowing conversion that keeps the date portion and simply
            discards everything else - the hour, minute, second, fractional-second, and, for
            DATETIMEOFFSET, the offset itself. No error is raised; the row commits with a value
            that looks entirely valid, just silently missing information the source value carried.

            This differs from the numeric write-loss cases in one important way: the loss isn't a
            rounding of the same quantity, it's the outright disappearance of an entire dimension
            of the data. 2024-03-15T23:47:12.500 and 2024-03-15T00:00:01.000 both collapse to the
            identical DATE value 2024-03-15 - two events that happened almost a full day apart
              become indistinguishable once persisted. For any table where the time-of-day genuinely
            matters - an audit log, an SLA deadline, an appointment slot, anything ordered or
            compared at sub-day granularity - this silently destroys the ability to answer "when,
            precisely" after the fact, and there's no way to recover the discarded time component
            from the DATE column alone.

            It's also easy to introduce by accident: GETDATE()/SYSDATETIME() return a full
            DATETIME2/DATETIME value, and any code path that passes that straight into a column
            that was modeled (or later narrowed) to DATE loses the time silently, often without the
            author realizing the column's type had ever been anything other than "the current
            timestamp."
            """,
        HowToFixIt: """
            If the time-of-day is meaningful for this column, widen it back to a temporal type that
            retains it - DATETIME2 with an explicit precision is usually the right choice for new
            work, since it also avoids DATETIME's own known rounding-to-1/300-second quirk. If the
            date-only value is genuinely intended - the column really only ever needs a calendar
            date, not a moment in time - make that explicit by CASTing the source value to DATE at
            the call site, so a reader of the query sees the truncation as a deliberate decision
            rather than an invisible consequence of the column's declared type.
            """,
        Examples:
        [
            new RuleDocExample(
                Title: "The current timestamp written into a DATE column",
                NoncompliantSql: """
                    CREATE TABLE dbo.Appointments
                    (
                        AppointmentId INT  NOT NULL PRIMARY KEY,
                        ScheduledFor  DATE NOT NULL
                    );

                    DECLARE @requestedAt DATETIME2(3) = '2024-03-15T14:30:00.000';

                    UPDATE dbo.Appointments
                    SET ScheduledFor = @requestedAt
                    WHERE AppointmentId = 1;
                    """,
                NoncompliantExplanation: "@requestedAt carries a 14:30 time component that ScheduledFor, being DATE, has no room to store - the assignment silently keeps only 2024-03-15 and drops the time entirely.",
                CompliantSql: """
                    CREATE TABLE dbo.Appointments
                    (
                        AppointmentId INT         NOT NULL PRIMARY KEY,
                        ScheduledFor  DATETIME2(3) NOT NULL
                    );

                    DECLARE @requestedAt DATETIME2(3) = '2024-03-15T14:30:00.000';

                    UPDATE dbo.Appointments
                    SET ScheduledFor = @requestedAt
                    WHERE AppointmentId = 1;
                    """,
                CompliantExplanation: "ScheduledFor is now DATETIME2(3), the same precision as the source value, so the 14:30 time-of-day is preserved instead of being silently dropped."),
        ]);
}
