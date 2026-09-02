using SilentScan.Core.Reporting.Sarif;

namespace SilentScan.Core.Reporting.RuleDocs.SessionDate;

internal static class AmbiguousDateLiteralConversion
{
    public static string RuleId => SarifRuleCatalog.AmbiguousDateLiteralConversionRuleId;

    public static RuleDocContent Content { get; } = new(
        WhyItMatters: """
            A `CAST` or a style-less `CONVERT` of a string literal to a date/time type is resolved
            using the session's own `DATEFORMAT`/default language at the moment the statement runs -
            for an AMBIGUOUS literal like `'03/04/2026'`, where the day and month positions could each
            plausibly be either number, that means the exact same literal, in the exact same module,
            resolves to a different real date depending purely on session state the module's own text
            never mentions.

            This is a broader risk than the sibling `SET DATEFORMAT`/`SET DATEFIRST` rules: those fire
            only when the module's own body contains an explicit `SET DATEFORMAT`. Oracle-confirmed
            directly that the ambiguity exists with no `SET DATEFORMAT` anywhere at all - with the
            session language left at `us_english`, `CAST('02/03/2024' AS date)` resolves to February
            3; after only `SET LANGUAGE British` (still no `SET DATEFORMAT` statement anywhere), the
            identical literal resolves to March 2. A caller connecting with a different default
            language, or a later change to the connection's own settings, silently changes what this
            module's own literal means - with nothing in the module's own text to reveal the
            dependency.

            A `CONVERT` call that supplies an explicit style code (for example, style 103 for
            `dd/mm/yyyy`) is unaffected: the style code fixes the interpretation regardless of session
            state, so it is not flagged.
            """,
        HowToFixIt: """
            Use an unambiguous ISO date literal format (YYYYMMDD) instead of a slash/dot/dash-
            separated literal whose day and month positions could each be either number - ISO format
            parses identically regardless of the session's DATEFORMAT or default language.
            """,
        Examples:
        [
            new RuleDocExample(
                Title: "An ambiguous literal cast to date with no SET DATEFORMAT anywhere",
                NoncompliantSql: """
                    CREATE PROCEDURE dbo.GetEventsOnDate AS
                    BEGIN
                        SELECT EventId FROM dbo.Events WHERE EventDate = CAST('02/03/2024' AS date);
                    END;
                    """,
                NoncompliantExplanation: "With the caller's session language at us_english this resolves to February 3, 2024; with British instead, the identical literal resolves to March 2, 2024 - a silent, caller-dependent correctness bug with no SET DATEFORMAT statement anywhere in this procedure to reveal the dependency.",
                CompliantSql: """
                    CREATE PROCEDURE dbo.GetEventsOnDate AS
                    BEGIN
                        SELECT EventId FROM dbo.Events WHERE EventDate = CAST('20240203' AS date);
                    END;
                    """,
                CompliantExplanation: "The ISO YYYYMMDD literal parses to February 3, 2024 regardless of the caller's session DATEFORMAT or default language - no ambiguity, nothing to depend on."),
        ]);
}
