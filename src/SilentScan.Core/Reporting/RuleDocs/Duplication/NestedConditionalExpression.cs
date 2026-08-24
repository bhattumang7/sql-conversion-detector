using SilentScan.Core.Predicates;
using SilentScan.Core.Reporting.Sarif;

namespace SilentScan.Core.Reporting.RuleDocs.Duplication;

internal static class NestedConditionalExpression
{
    public static string RuleId => SarifRuleCatalog.DuplicationRuleId(DuplicationFindingKind.NestedConditionalExpression);

    public static RuleDocContent Content { get; } = new(
        WhyItMatters: """
            An IIF call is nested inside another IIF call's own THEN or ELSE branch - T-SQL's
            equivalent of a nested ternary expression. Nested IIFs read as a single dense expression
            with no visual structure separating the distinct conditions being tested.
            """,
        Examples:
        [
            new RuleDocExample(
                Title: "An IIF nested inside another IIF's branch",
                NoncompliantSql: "SELECT IIF(Status = 'Active', IIF(Region = 'US', 'A-US', 'A-Other'), 'Inactive') AS Code FROM dbo.Orders;",
                NoncompliantExplanation: "The inner IIF is nested inside the outer IIF's THEN branch, producing one dense expression with three distinct outcomes packed together.",
                CompliantSql: """
                    SELECT CASE
                        WHEN Status = 'Active' AND Region = 'US' THEN 'A-US'
                        WHEN Status = 'Active' THEN 'A-Other'
                        ELSE 'Inactive'
                    END AS Code
                    FROM dbo.Orders;
                    """,
                CompliantExplanation: "A CASE expression lays out all three outcomes as separate branches instead of nested calls."),
        ]);
}
