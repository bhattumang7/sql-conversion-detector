using SilentScan.Core.Predicates;
using SilentScan.Core.Reporting.Sarif;

namespace SilentScan.Core.Reporting.RuleDocs.SessionDate;

internal static class SetDateFormat
{
    public static string RuleId => SarifRuleCatalog.SessionDateSettingRuleId(SessionDateSettingKind.DateFormat);

    public static RuleDocContent Content { get; } = new(
        WhyItMatters: """
            `SET DATEFORMAT` changes how the session interprets an AMBIGUOUS string date literal -
            one where the day and month could each plausibly be either number, like `'03/04/2026'`.
            Under `mdy` that string means March 4th; under `dmy` it means April 3rd - the exact same
            literal, the exact same statement, a genuinely different date depending purely on which
            `DATEFORMAT` happened to be in effect in the session at the moment the statement ran.
            Oracle-confirmed directly: the identical ambiguous literal resolves to two different real
            dates purely as a function of this setting.

            Placing `SET DATEFORMAT` inside a module's own body is the dangerous case this rule
            targets, because it makes the module's own behavior depend on session state the module
            itself changed - and that state change persists for the rest of the session, silently
            affecting every other ambiguous literal parsed afterward, including ones in code the
            module's author never touched. A caller with a different `DATEFORMAT` already set, or a
            later batch in the same session, can see the SAME literal resolve differently depending
            on execution order - a correctness bug that never shows up in isolated testing of the
            module alone.
            """,
        HowToFixIt: """
            Use an unambiguous ISO date literal format (YYYYMMDD) instead of relying on SET
            DATEFORMAT to resolve an ambiguous string date - ISO format parses identically regardless
            of the session's DATEFORMAT setting.
            """,
        Examples:
        [
            new RuleDocExample(
                Title: "SET DATEFORMAT changing how a later ambiguous literal resolves",
                NoncompliantSql: """
                    CREATE PROCEDURE dbo.GetEventsInMonth AS
                    BEGIN
                        SET DATEFORMAT mdy;
                        SELECT EventId FROM dbo.Events WHERE EventDate = '03/04/2026';
                    END;
                    """,
                NoncompliantExplanation: "Under mdy this literal means March 4, 2026; under a session already set to dmy before this procedure ran, or a future edit that changes/removes this SET, the very same literal means April 3 - a silent, execution-order-dependent correctness bug.",
                CompliantSql: """
                    CREATE PROCEDURE dbo.GetEventsInMonth AS
                    BEGIN
                        SELECT EventId FROM dbo.Events WHERE EventDate = '20260304';
                    END;
                    """,
                CompliantExplanation: "The ISO YYYYMMDD literal parses to March 4, 2026 regardless of the session's DATEFORMAT setting - no ambiguity, nothing to depend on."),
        ]);
}
