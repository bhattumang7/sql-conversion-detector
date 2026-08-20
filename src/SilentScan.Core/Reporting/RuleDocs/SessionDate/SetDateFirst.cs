using SilentScan.Core.Predicates;
using SilentScan.Core.Reporting.Sarif;

namespace SilentScan.Core.Reporting.RuleDocs.SessionDate;

internal static class SetDateFirst
{
    public static string RuleId => SarifRuleCatalog.SessionDateSettingRuleId(SessionDateSettingKind.DateFirst);

    public static RuleDocContent Content { get; } = new(
        WhyItMatters: """
            `SET DATEFIRST` changes which day of the week the session considers day 1 for
            `DATEPART(weekday, ...)`/`DATENAME(weekday, ...)` purposes - by default Sunday is 1, but
            `SET DATEFIRST 1` makes Monday day 1 instead, shifting every weekday ordinal by one.
            Oracle-confirmed directly: `DATEPART(weekday, ...)` for a fixed, unchanging real date
            returns a different ordinal purely as a function of which `DATEFIRST` value is in effect,
            with no change to the date or the query itself.

            As with `SET DATEFORMAT`, the risk is specifically a module changing this session-level
            setting inside its own body: the change persists for the rest of the session, so any
            weekday computation elsewhere in the same session - including code the module's own
            author never touched - can silently see a different DATEFIRST value than it expects,
            depending purely on execution order.
            """,
        HowToFixIt: """
            Avoid relying on the session's own SET DATEFIRST value for weekday math - use a
            DATEFIRST-independent computation instead (for example, deriving the weekday from a
            fixed reference date via DATEDIFF rather than DATEPART(weekday, ...)).
            """,
        Examples:
        [
            new RuleDocExample(
                Title: "SET DATEFIRST changing a later weekday computation's result",
                NoncompliantSql: """
                    CREATE PROCEDURE dbo.IsMonday AS
                    BEGIN
                        SET DATEFIRST 1;
                        SELECT CASE WHEN DATEPART(weekday, GETDATE()) = 1 THEN 1 ELSE 0 END;
                    END;
                    """,
                NoncompliantExplanation: "With DATEFIRST 1, weekday 1 means Monday; if a caller's session (or a later edit) has a different DATEFIRST in effect, the same DATEPART(weekday, ...) = 1 comparison silently tests for a different day of the week.",
                CompliantSql: """
                    CREATE PROCEDURE dbo.IsMonday AS
                    BEGIN
                        -- Day 0 (1900-01-01) is itself a Monday, so DATEDIFF(day, 0, d) % 7 = 0 means d is a Monday.
                        SELECT CASE WHEN DATEDIFF(day, 0, GETDATE()) % 7 = 0 THEN 1 ELSE 0 END;
                    END;
                    """,
                CompliantExplanation: "DATEDIFF against the fixed reference date 0 (1900-01-01, itself a Monday) computes the weekday independent of the session's own DATEFIRST setting."),
        ]);
}
