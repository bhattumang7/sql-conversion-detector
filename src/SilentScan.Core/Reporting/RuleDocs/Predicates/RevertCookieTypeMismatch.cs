using SilentScan.Core.Reporting.Sarif;

namespace SilentScan.Core.Reporting.RuleDocs.Predicates;

internal static class RevertCookieTypeMismatch
{
    public static string RuleId => SarifRuleCatalog.RevertCookieTypeMismatchRuleId;

    public static RuleDocContent Content { get; } = new(
        WhyItMatters: """
            REVERT WITH COOKIE = @cookie exists to undo an EXECUTE AS ... WITH COOKIE INTO @cookie
            impersonation, and the engine ties the two together through a fixed binary shape rather than
            just a variable name - the COOKIE INTO clause always produces a varbinary(100) value, and
            REVERT only accepts a cookie of exactly that shape back. Oracle-confirmed against a real
            engine: declaring the cookie variable as varbinary(10) and passing it to REVERT WITH COOKIE
            fails to compile with Msg 15533 ("Invalid data type is supplied in the 'Revert' statement"),
            even though varbinary(10) is a perfectly ordinary, otherwise-legal declaration. Any declared
            type other than the exact varbinary(100) shape - a different fixed length, a MAX length, or a
            different type entirely - is rejected the same way, and the declared type is knowable purely
            from the variable's own DECLARE or parameter definition, with no catalog or runtime data
            needed.
            """,
        HowToFixIt: """
            Declare the cookie variable as varbinary(100), matching the shape COOKIE INTO actually
            produces and the shape REVERT actually requires.
            """,
        Examples:
        [
            new RuleDocExample(
                Title: "A cookie variable declared narrower than varbinary(100) never compiles",
                NoncompliantSql: """
                    DECLARE @cookie varbinary(10);
                    EXECUTE AS USER = 'dbo' WITH COOKIE INTO @cookie;
                    REVERT WITH COOKIE = @cookie;
                    """,
                NoncompliantExplanation: "Fails to compile with Msg 15533 (\"Invalid data type is supplied in the 'Revert' statement\") - REVERT only accepts a varbinary(100) cookie.",
                CompliantSql: """
                    DECLARE @cookie varbinary(100);
                    EXECUTE AS USER = 'dbo' WITH COOKIE INTO @cookie;
                    REVERT WITH COOKIE = @cookie;
                    """,
                CompliantExplanation: "The declared type matches the fixed varbinary(100) shape the engine produces and requires."),
        ]);
}
