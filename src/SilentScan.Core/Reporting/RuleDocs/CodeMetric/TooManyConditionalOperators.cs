using SilentScan.Core.Predicates;
using SilentScan.Core.Reporting.Sarif;

namespace SilentScan.Core.Reporting.RuleDocs.CodeMetric;

internal static class TooManyConditionalOperators
{
    public static string RuleId => SarifRuleCatalog.CodeMetricRuleId(CodeMetricFindingKind.TooManyConditionalOperators);

    public static RuleDocContent Content { get; } = new(
        WhyItMatters: """
            A single IF/WHILE condition chains more AND/OR operators than the configured maximum.
            Purely a readability signal - no query result or execution plan is affected. A long
            chain of mixed AND/OR is easy to misread, especially once operator precedence starts
            mattering, and hard to verify is actually testing what it was meant to test.
            """,
        HowToFixIt: """
            Extract named intermediate boolean variables for sub-conditions, or split the condition
            into nested checks, so each individual test reads clearly on its own.
            """,
        Examples:
        [
            new RuleDocExample(
                Title: "A single condition chaining many AND/OR operators",
                NoncompliantSql: """
                    IF @status = 'Active' AND @balance > 0 AND @region = 'US' AND @tier = 'Gold' OR @override = 1
                    BEGIN
                        SELECT 1;
                    END
                    """,
                NoncompliantExplanation: "Five chained AND/OR operators in one condition make the actual grouping (and which combination trips the OR @override = 1 branch) hard to verify at a glance.",
                CompliantSql: """
                    DECLARE @isEligibleUsGoldCustomer BIT = CASE
                        WHEN @status = 'Active' AND @balance > 0 AND @region = 'US' AND @tier = 'Gold' THEN 1
                        ELSE 0
                    END;

                    IF @isEligibleUsGoldCustomer = 1 OR @override = 1
                    BEGIN
                        SELECT 1;
                    END
                    """,
                CompliantExplanation: "Naming the multi-part eligibility check separately from the override makes the final condition's real structure explicit."),
        ]);
}
