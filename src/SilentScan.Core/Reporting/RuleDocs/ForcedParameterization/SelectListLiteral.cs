using SilentScan.Core.Predicates;
using SilentScan.Core.Reporting.Sarif;

namespace SilentScan.Core.Reporting.RuleDocs.ForcedParameterization;

internal static class SelectListLiteral
{
    public static string RuleId => SarifRuleCatalog.ForcedParameterizationRuleId(ForcedParameterizationFindingKind.SelectListLiteral);

    public static RuleDocContent Content { get; } = new(
        WhyItMatters: """
            Reported only when the target database has `PARAMETERIZATION FORCED` on. Confirmed
            directly against a real engine: `SELECT 'MarkerSelectList', Id FROM T WHERE Id = 1`
            keeps the select-list literal untouched in the cached plan while the WHERE-clause
            equality correctly parameterizes.

            A tag/label literal returned alongside real columns (a common shape for
            UNION-distinguishing constants, or a status label) is a minor case in isolation, but
            it means that specific call site never shares a plan with a differently-tagged
            sibling query under PARAMETERIZATION FORCED, even though the two are otherwise
            identical.
            """,
        HowToFixIt: """
            Pass the select-list literal as a parameter or local variable instead of a literal, or
            accept that this specific query text always gets its own plan.
            """,
        Examples:
        [
            new RuleDocExample(
                Title: "A literal tag column in the select list",
                NoncompliantSql: """
                    SELECT 'Active', CustomerId FROM dbo.Customers WHERE Status = 1;
                    """,
                NoncompliantExplanation: "Under PARAMETERIZATION FORCED, 'Active' stays literal in the cached plan - a sibling query tagging 'Inactive' compiles as a fully separate plan, not a shared one.",
                CompliantSql: """
                    CREATE PROCEDURE dbo.GetCustomersByStatus @Tag varchar(20), @StatusId int AS
                    SELECT @Tag, CustomerId FROM dbo.Customers WHERE Status = @StatusId;
                    """,
                CompliantExplanation: "The tag is already a parameter, so every call - regardless of tag value - shares the one compiled plan."),
        ]);
}
