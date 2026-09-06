using SilentScan.Core.Reporting.Sarif;

namespace SilentScan.Core.Reporting.RuleDocs.Predicates;

internal static class RevertCookieTypeMismatch
{
    public static string RuleId => SarifRuleCatalog.RevertCookieTypeMismatchRuleId;

    public static RuleDocContent Content { get; } = new(
        WhyItMatters: """
            REVERT WITH COOKIE = @cookie exists to undo an EXECUTE AS ... WITH COOKIE INTO @cookie
            impersonation, and the engine requires the cookie variable to be a fixed-length varbinary
            of at least 50 bytes - not a narrower fixed length, and not varbinary(max). Oracle-confirmed
            against a real engine: declaring the cookie variable as varbinary(10) and passing it to
            REVERT WITH COOKIE fails to compile with Msg 15533 ("Invalid data type is supplied in the
            'Revert' statement"), even though varbinary(10) is a perfectly ordinary, otherwise-legal
            declaration - and the failure threshold is exact: varbinary(49) still fails, varbinary(50)
            succeeds. varbinary(100) is the width Microsoft's own EXECUTE AS documentation shows and the
            conventional choice, but it is not a hard requirement - oracle-confirmed varbinary(50)
            through at least varbinary(8000) all round-trip successfully through EXECUTE AS ... WITH
            COOKIE INTO and REVERT WITH COOKIE. A different type entirely, a narrower fixed length, or a
            MAX length is rejected the same way (Msg 15533), and the declared type is knowable purely
            from the variable's own DECLARE or parameter definition, with no catalog or runtime data
            needed.
            """,
        HowToFixIt: """
            Declare the cookie variable as a fixed-length varbinary of at least 50 bytes - varbinary(100)
            is the conventional choice - and never varbinary(max) or a narrower fixed length.
            """,
        Examples:
        [
            new RuleDocExample(
                Title: "A cookie variable declared narrower than the 50-byte minimum never compiles",
                NoncompliantSql: """
                    DECLARE @cookie varbinary(10);
                    EXECUTE AS USER = 'dbo' WITH COOKIE INTO @cookie;
                    REVERT WITH COOKIE = @cookie;
                    """,
                NoncompliantExplanation: "Fails to compile with Msg 15533 (\"Invalid data type is supplied in the 'Revert' statement\") - REVERT rejects any fixed varbinary length under 50 bytes.",
                CompliantSql: """
                    DECLARE @cookie varbinary(100);
                    EXECUTE AS USER = 'dbo' WITH COOKIE INTO @cookie;
                    REVERT WITH COOKIE = @cookie;
                    """,
                CompliantExplanation: "varbinary(100) is a fixed length at or above the 50-byte minimum the engine actually requires."),
        ]);
}
