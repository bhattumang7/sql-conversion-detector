using SilentScan.Core.Predicates;
using SilentScan.Core.Reporting.Sarif;

namespace SilentScan.Core.Reporting.RuleDocs.Declaration;

internal static class UndersizedVariableOrParameter
{
    public static string RuleId => SarifRuleCatalog.UndersizedDeclarationRuleId(UndersizedDeclarationSite.Declaration);

    public static RuleDocContent Content { get; } = new(
        WhyItMatters: """
            The DECLARE-site sibling of the undersized-column rule: a `DECLARE`d local variable or a
            procedure/function's own formal parameter, declared as a string or binary type with a
            length of 1 or 2, is the same advisory code smell reported for the same reason - almost
            always a truncated-from-a-larger-source mistake or a leftover single-character
            placeholder, needing no comparison against any other value to be worth a second look.

            A parameter declared this narrow is particularly easy to miss in review, since the
            symptom doesn't show up where the parameter is declared - it shows up later, wherever a
            caller passes a real value that gets silently truncated to fit, or wherever the
            procedure body itself tries to build something longer into the variable and loses
            characters with no error. Like its table-column sibling, this is purely an
            advisory/structural judgment call, reported at Low confidence.
            """,
        HowToFixIt: """
            Confirm the variable's or parameter's real intended domain and widen its declared length
            if 1 or 2 characters genuinely isn't enough for the data it's meant to hold.
            """,
        Examples:
        [
            new RuleDocExample(
                Title: "A procedure parameter declared with a 2-character string length",
                NoncompliantSql: """
                    CREATE PROCEDURE dbo.usp_SetCustomerCode
                        @Code VARCHAR(2)
                    AS
                    BEGIN
                        UPDATE dbo.Customers SET Code = @Code WHERE Id = 1;
                    END;
                    """,
                NoncompliantExplanation: "@Code declared as VARCHAR(2) can hold at most 2 characters - a caller passing a longer real code value is silently truncated with no error at the call boundary.",
                CompliantSql: """
                    CREATE PROCEDURE dbo.usp_SetCustomerCode
                        @Code VARCHAR(20)
                    AS
                    BEGIN
                        UPDATE dbo.Customers SET Code = @Code WHERE Id = 1;
                    END;
                    """,
                CompliantExplanation: "Widened to a length that matches the real domain of customer codes, so no caller's value is silently truncated."),
        ]);
}
