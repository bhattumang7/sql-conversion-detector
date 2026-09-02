using SilentScan.Core.Predicates;
using SilentScan.Core.Reporting.Sarif;

namespace SilentScan.Core.Reporting.RuleDocs.Database;

internal static class PlanGuideAltersOptimization
{
    public static string RuleId => SarifRuleCatalog.DatabaseConfigurationRuleId(DatabaseConfigurationFindingKind.PlanGuideAltersOptimization);

    public static RuleDocContent Content { get; } = new(
        WhyItMatters: """
            An enabled row in `sys.plan_guides` always carries its own `@hints` (or a fixed
            `@plan_xml`/`USE PLAN` plan) that the optimizer substitutes for its normal choice on
            matching SQL, without the query text at the call site ever changing - reading the
            procedure or ad-hoc statement alone gives no hint that a plan guide is silently
            overriding how it compiles.

            This is informational, not a defect: plan guides are a deliberate, real tool teams
            reach for to work around a specific plan regression, and this rule exists purely to
            surface that one is active so it isn't forgotten once the regression it was created for
            is gone. This is a database-level fact, read once per scan directly from
            `sys.plan_guides` - only available when scanning a live, connected target, since there
            is no file-mode equivalent of "which plan guides are currently active."
            """,
        HowToFixIt: """
            Confirm the plan guide is still needed for the query/object it targets. Drop it with
            `sp_control_plan_guide` once the underlying plan issue it was created for is resolved.
            """,
        Examples:
        [
            new RuleDocExample(
                Title: "An enabled plan guide overriding optimization for a specific statement",
                NoncompliantSql: """
                    EXEC sp_create_plan_guide
                        @name = N'PG_Example',
                        @stmt = N'SELECT Id FROM dbo.T WHERE Id = 1',
                        @type = N'SQL',
                        @module_or_batch = NULL,
                        @params = NULL,
                        @hints = N'OPTION (RECOMPILE)';
                    """,
                NoncompliantExplanation: "The matching statement now always compiles with RECOMPILE forced by the plan guide, with nothing in the statement's own text showing it.",
                CompliantSql: """
                    EXEC sp_control_plan_guide @operation = N'DROP', @name = N'PG_Example';
                    """,
                CompliantExplanation: "Once the plan guide is dropped, the statement compiles under the optimizer's own normal behavior again."),
        ]);
}
