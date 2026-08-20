using SilentScan.Core.Predicates;
using SilentScan.Core.Reporting.Sarif;

namespace SilentScan.Core.Reporting.RuleDocs.ControlFlow;

internal static class CaseExpressionMissingElse
{
    public static string RuleId => SarifRuleCatalog.ControlFlowRiskRuleId(ControlFlowRiskFindingKind.CaseExpressionMissingElse);

    public static RuleDocContent Content { get; } = new(
        WhyItMatters: """
            A simple `CASE <input> WHEN v1 THEN ... WHEN v2 THEN ... END` with no `ELSE` silently
            evaluates to `NULL` whenever the input matches none of the listed WHEN values - no error,
            no warning, directly oracle-confirmed by executing exactly this shape against a real
            engine. For a fixed, enumerable value list (the defining feature of a simple CASE, unlike
            a searched CASE's typically-open-ended boolean conditions), "did I forget to list a
            value" is a genuinely common, easy-to-make mistake, and the silent-NULL fallthrough means
            that mistake produces no signal anywhere - the query just returns NULL for whatever rows
            hit the gap.

            The searched-CASE form (`CASE WHEN cond THEN ...`) has the identical fallthrough-to-NULL
            behavior but is deliberately NOT covered by this rule - a searched CASE's own boolean
            conditions are typically deliberately partial or mutually-exclusive-by-design (unlike a
            simple CASE's fixed value list), so flagging every searched CASE without an ELSE would
            fire constantly on completely ordinary, intentional T-SQL and add more noise than signal.
            Narrowing to the simple-CASE form only is what keeps this a high-precision finding.
            """,
        HowToFixIt: """
            Add an explicit ELSE branch to the simple CASE expression, even if it's just ELSE NULL to
            make the fallthrough intentional and visible rather than implicit.
            """,
        Examples:
        [
            new RuleDocExample(
                Title: "A simple CASE with no ELSE and an unlisted status value",
                NoncompliantSql: """
                    SELECT
                        CASE Status
                            WHEN 1 THEN 'Active'
                            WHEN 2 THEN 'Inactive'
                        END AS StatusName
                    FROM dbo.Accounts;
                    """,
                NoncompliantExplanation: "A row with Status = 3 (or any value other than 1 or 2) silently evaluates to NULL - no error, no warning, and nothing in the query text signals that a status value was never accounted for.",
                CompliantSql: """
                    SELECT
                        CASE Status
                            WHEN 1 THEN 'Active'
                            WHEN 2 THEN 'Inactive'
                            ELSE 'Unknown'
                        END AS StatusName
                    FROM dbo.Accounts;
                    """,
                CompliantExplanation: "The explicit ELSE makes the fallthrough case visible and intentional instead of an implicit, easy-to-miss NULL."),
        ]);
}
