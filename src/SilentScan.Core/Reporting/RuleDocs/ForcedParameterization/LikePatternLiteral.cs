using SilentScan.Core.Predicates;
using SilentScan.Core.Reporting.Sarif;

namespace SilentScan.Core.Reporting.RuleDocs.ForcedParameterization;

internal static class LikePatternLiteral
{
    public static string RuleId => SarifRuleCatalog.ForcedParameterizationRuleId(ForcedParameterizationFindingKind.LikePatternLiteral);

    public static RuleDocContent Content { get; } = new(
        WhyItMatters: """
            Reported only when the target database has `PARAMETERIZATION FORCED` on - a deliberate
            setting meant to stop per-literal ad-hoc plan-cache bloat by having the engine treat
            every literal in a query as if it were a parameter, sharing one compiled plan across
            calls that only differ by value.

            Confirmed directly against a real engine: under `PARAMETERIZATION FORCED`,
            `WHERE Id = 42 AND Name LIKE 'abc%'` compiles to a shared `(@0 int) ... WHERE Id = @0
            AND Name LIKE 'abc%'` plan - the equality parameterizes, the LIKE pattern does not.
            This is a real, documented exception, not a bug in the engine - but it means a search
            query varying only its LIKE pattern (the overwhelmingly common real-world shape for a
            search box) gets a fresh compile per distinct pattern regardless of the setting, right
            where the setting was expected to help most.
            """,
        HowToFixIt: """
            Pass the LIKE pattern as a parameter or local variable instead of a literal - the
            engine has no exclusion for a LIKE predicate whose pattern is already a parameter.
            """,
        Examples:
        [
            new RuleDocExample(
                Title: "A LIKE pattern hard-coded as a literal",
                NoncompliantSql: """
                    SELECT CustomerId, Name FROM dbo.Customers WHERE Name LIKE 'Smith%';
                    """,
                NoncompliantExplanation: "Under PARAMETERIZATION FORCED, the pattern stays literal in the cached plan - a search for 'Jones%' next recompiles instead of reusing this plan.",
                CompliantSql: """
                    CREATE PROCEDURE dbo.SearchCustomersByName @Pattern varchar(100) AS
                    SELECT CustomerId, Name FROM dbo.Customers WHERE Name LIKE @Pattern;
                    """,
                CompliantExplanation: "The pattern is already a parameter, so there is nothing for this exclusion to apply to - every call shares the one compiled plan regardless of PARAMETERIZATION FORCED."),
        ]);
}
