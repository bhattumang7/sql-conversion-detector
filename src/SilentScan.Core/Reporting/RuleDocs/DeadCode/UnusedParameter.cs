using SilentScan.Core.Predicates;
using SilentScan.Core.Reporting.Sarif;

namespace SilentScan.Core.Reporting.RuleDocs.DeadCode;

internal static class UnusedParameter
{
    public static string RuleId => SarifRuleCatalog.DeadCodeRuleId(DeadCodeFindingKind.UnusedParameter);

    public static RuleDocContent Content { get; } = new(
        WhyItMatters: """
            A non-OUTPUT formal parameter that the routine body never references does nothing once
            inside the routine - callers can still pass it, but the value is discarded. This usually
            means the parameter was left behind after the logic that used it was removed, or a caller
            is passing a value under the mistaken belief that it changes the routine's behavior when
            it does not.
            """,
        HowToFixIt: """
            Delete the unused parameter (and update callers), or add the reference that was meant to
            use it inside the routine body.
            """,
        Examples:
        [
            new RuleDocExample(
                Title: "A parameter the routine body never references",
                NoncompliantSql: """
                    CREATE PROCEDURE dbo.GetCustomer (@customerId INT, @unused INT) AS
                    BEGIN
                        SELECT @customerId;
                    END
                    """,
                NoncompliantExplanation: "@unused is never referenced anywhere in the body - callers can pass it, but it has no effect on what the procedure does.",
                CompliantSql: """
                    CREATE PROCEDURE dbo.GetCustomer (@customerId INT) AS
                    BEGIN
                        SELECT @customerId;
                    END
                    """,
                CompliantExplanation: "The unused parameter is removed; the procedure's real behavior is unchanged."),
        ]);
}
